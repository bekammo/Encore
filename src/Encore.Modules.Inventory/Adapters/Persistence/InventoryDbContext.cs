using Encore.Modules.Inventory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Inventory.Adapters.Persistence;

/// <summary>
/// Every save writes each tracked seat's domain events as outbox rows in the same
/// transaction, so a change and its announcement commit together or not at all (015).
/// </summary>
public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options)
    : DbContext(options)
{
    private readonly List<OutboxMessage> _drained = [];

    public DbSet<Seat> Seats => Set<Seat>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(InventoryPersistence.Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(InventoryDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }

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

    /// <summary>Overridden too, so no save path can skip the drain.</summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var drained = DrainDomainEvents();

        var written = base.SaveChanges(acceptAllChangesOnSuccess);

        MarkPublished(drained);

        return written;
    }

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
            // Added in raised order. Delivery does not keep it (024).
            foreach (var domainEvent in seat.DomainEvents)
            {
                var message = SeatEventPublication.ToOutboxMessage(domainEvent);

                OutboxMessages.Add(message);
                _drained.Add(message);
            }
        }

        return raising;
    }

    // A rejected save leaves its drained rows Added. Detach them so the retry does not publish
    // the events twice; rows added by anyone else are left alone.
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

    // Only after the base save, so a rejected save keeps its events for the retry; without it,
    // later saves on this context would write them again (015).
    private static void MarkPublished(List<Seat> drained)
    {
        foreach (var seat in drained)
        {
            seat.ClearDomainEvents();
        }
    }
}
