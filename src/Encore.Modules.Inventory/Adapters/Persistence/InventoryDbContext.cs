using Encore.Modules.Inventory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Inventory.Adapters.Persistence;

/// <summary>
/// EF Core context for the <c>inventory</c> schema: seats and the outbox.
/// </summary>
/// <remarks>
/// Owns the outbox drain. Every save writes the domain events of every tracked seat as
/// outbox rows in the same transaction, so a state change and its announcement commit
/// together or not at all.
/// </remarks>
public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options)
    : DbContext(options)
{
    /// <summary>Outbox rows this context's drain added and has not yet seen committed.</summary>
    private readonly List<OutboxMessage> _drained = [];

    public DbSet<Seat> Seats => Set<Seat>();

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
    /// <remarks>Overridden too, so no save path can skip the drain.</remarks>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var drained = DrainDomainEvents();

        var written = base.SaveChanges(acceptAllChangesOnSuccess);

        MarkPublished(drained);

        return written;
    }

    /// <summary>
    /// Adds every tracked seat's events to the outbox and returns the seats drained.
    /// Walks the change tracker because one scoped context can hold several seats.
    /// </summary>
    private List<Seat> DrainDomainEvents()
    {
        DiscardRejectedOutboxRows();

        // Materialised first: adding rows while enumerating the tracker would throw.
        var raising = ChangeTracker.Entries<Seat>()
            .Select(entry => entry.Entity)
            .Where(seat => seat.DomainEvents.Count > 0)
            .ToList();

        foreach (var seat in raising)
        {
            // In raised order, so a reclaim's SeatReleased precedes its SeatHeld.
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
    /// Detaches outbox rows this drain added for a save that was rejected, so a retry
    /// does not publish the same events twice. Rows added by anyone else are left alone.
    /// </summary>
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

        _drained.Clear();
    }

    /// <summary>
    /// Clears the events of seats whose rows have committed. Without this, later saves on
    /// the same context would write them again. Runs after the base save, so a rejected
    /// save keeps its events for the retry.
    /// </summary>
    private static void MarkPublished(List<Seat> drained)
    {
        foreach (var seat in drained)
        {
            seat.ClearDomainEvents();
        }
    }
}
