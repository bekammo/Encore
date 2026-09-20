using Encore.Modules.Inventory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Inventory.Adapters.Persistence;

/// <summary>
/// EF Core context owning the <c>inventory</c> schema: seats, their concurrency
/// tokens, and the outbox table. Lives in Adapters because persistence is a
/// detail — the domain has never heard of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class owns the outbox drain.</b> Every <c>SaveChanges</c> collects the
/// domain events raised by every tracked <see cref="Seat"/> and writes them as
/// outbox rows before handing over to the base call, so the state change and the
/// announcement of it commit together or not at all. That single guarantee is the
/// whole reason an outbox exists, and <c>DECISIONS.md</c> 044 settles why the duty
/// is here rather than in <c>EfSeatRepository</c>: the repository is handed one
/// aggregate and this context commits all of them.
/// </para>
/// </remarks>
public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options)
    : DbContext(options)
{
    /// <summary>
    /// The outbox rows this context's drain has added and not yet seen committed.
    /// </summary>
    /// <remarks>
    /// Exists so the drain can tell its own rows from anybody else's when it discards
    /// the leavings of a rejected save. A <see cref="DbContext"/> is scoped and is not
    /// thread-safe to begin with, so a plain list is the right shape.
    /// </remarks>
    private readonly List<OutboxMessage> _drained = [];

    /// <summary>The seats, and the authoritative record of their state.</summary>
    public DbSet<Seat> Seats => Set<Seat>();

    /// <summary>Events awaiting publication, and the record of those already sent.</summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(InventoryPersistence.Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(InventoryDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }

    /// <inheritdoc />
    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        var drained = DrainDomainEvents();

        var written = await base
            .SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken)
            .ConfigureAwait(false);

        MarkPublished(drained);

        return written;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Nothing in this module saves synchronously, and this override exists so that
    /// nothing can start to without the drain coming along. An outbox whose
    /// completeness depends on callers preferring one method over another is an
    /// outbox that loses an event the first time somebody picks the other one.
    /// </remarks>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var drained = DrainDomainEvents();

        var written = base.SaveChanges(acceptAllChangesOnSuccess);

        MarkPublished(drained);

        return written;
    }

    /// <summary>
    /// Copies every tracked seat's raised events into the outbox, and reports which
    /// seats were drained so they can be cleared once the write has actually landed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Discovery walks the change tracker rather than being handed an aggregate.</b>
    /// A four-seat checkout drives four holds through one scoped context, so by the
    /// last of them the tracker holds four seats while each repository call knew
    /// about one. 044 has the full argument; the short version is that a drain whose
    /// completeness rests on the current call pattern stops being complete the first
    /// time the call pattern changes.
    /// </para>
    /// <para>
    /// <b><see cref="Seat"/> is named concretely</b> because it has no base class and
    /// no marker interface — <c>Encore.BuildingBlocks.Domain</c> was removed
    /// deliberately and the aggregate manages its own list. That is honest with one
    /// aggregate and wants a marker interface beside <c>IDomainEvent</c> when there
    /// is a second, rather than the base class that was removed.
    /// </para>
    /// </remarks>
    private List<Seat> DrainDomainEvents()
    {
        DiscardRejectedOutboxRows();

        // Materialised before a single row is added, and that is not a style
        // preference. ChangeTracker.Entries<T>() is a live view over the state
        // manager, so adding an OutboxMessage inside the loop invalidates the
        // enumerator and the next step throws "Collection was modified" — from
        // inside SaveChanges, on every write path in the module.
        var raising = ChangeTracker.Entries<Seat>()
            .Select(entry => entry.Entity)
            .Where(seat => seat.DomainEvents.Count > 0)
            .ToList();

        foreach (var seat in raising)
        {
            // In order. A lazy reclaim raises SeatReleased(Expired) for the old
            // holder before SeatHeld for the new one, and the sequence this
            // assigns is what keeps that pair reconstructable downstream (007).
            foreach (var domainEvent in seat.DomainEvents)
            {
                var message = SeatEventPublication.ToOutboxMessage(domainEvent);

                OutboxMessages.Add(message);
                _drained.Add(message);
            }
        }

        return raising;
    }

    /// <summary>
    /// Removes outbox rows left tracked as <see cref="EntityState.Added"/> by a save
    /// that was rejected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All three seat handlers retry once after losing a race, and the retry raises
    /// the same events again. Without this, the attempt that eventually succeeds
    /// would commit the rejected attempt's rows alongside its own and publish the
    /// hold twice. 044 named this hazard before there was any code to have it.
    /// </para>
    /// <para>
    /// <b>Only rows this drain added, which is narrower than it first looked.</b> The
    /// first version detached every <see cref="EntityState.Added"/> outbox row, on the
    /// reasoning that after a successful save they are <see cref="EntityState.Unchanged"/>
    /// so nothing else could be caught. That reasoning is wrong about anybody who adds
    /// an outbox row and saves it themselves: their row is <c>Added</c> too, and this
    /// silently threw it away rather than persisting it. Nothing in production does
    /// that today, which is exactly what makes it worth guarding — it is a trap laid
    /// for the next person rather than a bug with a symptom.
    /// </para>
    /// <para>
    /// Membership is by reference, so it identifies the instances this context drained
    /// rather than anything about their contents.
    /// </para>
    /// </remarks>
    private void DiscardRejectedOutboxRows()
    {
        if (_drained.Count is 0)
        {
            return;
        }

        var stale = ChangeTracker
            .Entries<OutboxMessage>()
            .Where(entry => entry.State is EntityState.Added && _drained.Contains(entry.Entity))
            .ToList();

        foreach (var entry in stale)
        {
            entry.State = EntityState.Detached;
        }

        // Whatever is left in the list either committed or was just detached, so the
        // list has no further claim on any of it.
        _drained.Clear();
    }

    /// <summary>
    /// Clears the events of every seat whose rows have now been committed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is not optional bookkeeping, and 044 left it open as though it were.</b>
    /// The context is scoped and the handlers clear only the seat they are about to
    /// transition, so without this a four-seat checkout would drain seat one's
    /// <c>SeatHeld</c> again on each of the three saves that follow — four rows for
    /// one hold. The handlers' defensive clear cannot reach it, because by then they
    /// are looking at a different seat.
    /// </para>
    /// <para>
    /// After the base call rather than before it, so a rejected save leaves the
    /// events on the instance for the retry to re-raise over.
    /// </para>
    /// </remarks>
    private static void MarkPublished(List<Seat> drained)
    {
        foreach (var seat in drained)
        {
            seat.ClearDomainEvents();
        }
    }
}
