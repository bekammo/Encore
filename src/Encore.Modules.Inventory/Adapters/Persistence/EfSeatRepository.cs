using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Inventory.Adapters.Persistence;

/// <summary>
/// EF Core / Postgres implementation of <see cref="ISeatRepository"/>. The <c>xmin</c>
/// token makes every save a conditional UPDATE; EF's concurrency exception is translated
/// into <see cref="ConcurrentSeatModificationException"/> so it never leaks through the port.
/// </summary>
public sealed class EfSeatRepository(InventoryDbContext context) : ISeatRepository
{
    private readonly InventoryDbContext _context = context;

    /// <inheritdoc />
    public async Task<Seat?> GetByIdAsync(Guid seatId, CancellationToken cancellationToken = default)
    {
        // Reload a tracked seat instead of returning EF's cached instance, or a retry
        // after a lost race would re-attempt with the stale token.
        var tracked = _context.ChangeTracker
            .Entries<Seat>()
            .FirstOrDefault(entry => entry.Entity.Id == seatId);

        if (tracked is null)
        {
            return await _context.Seats
                .SingleOrDefaultAsync(seat => seat.Id == seatId, cancellationToken)
                .ConfigureAwait(false);
        }

        await tracked.ReloadAsync(cancellationToken).ConfigureAwait(false);

        // Reload detaches the entry if the row is gone.
        return tracked.State is EntityState.Detached ? null : tracked.Entity;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Seat>> GetByIdsAsync(
        IReadOnlyCollection<Guid> seatIds,
        CancellationToken cancellationToken = default)
    {
        // Detach and re-read in one query, discarding whatever a failed attempt changed.
        var tracked = _context.ChangeTracker
            .Entries<Seat>()
            .Where(entry => seatIds.Contains(entry.Entity.Id))
            .ToList();

        foreach (var entry in tracked)
        {
            entry.State = EntityState.Detached;
        }

        return await _context.Seats
            .Where(seat => seatIds.Contains(seat.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveAsync(Seat seat, CancellationToken cancellationToken = default)
    {
        // The outbox drain runs inside SaveChanges (InventoryDbContext).
        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrentSeatModificationException(seat.Id, ex);
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(IReadOnlyCollection<Seat> seats, CancellationToken cancellationToken = default)
    {
        // One SaveChanges: one transaction, sent as one batch.
        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            var lost = ex.Entries.Select(entry => entry.Entity).OfType<Seat>().FirstOrDefault()
                ?? seats.First();

            throw new ConcurrentSeatModificationException(lost.Id, ex);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<Guid>> FindLiveHoldsAsync(
        Guid clientId,
        Guid eventId,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
        // Expiry is in the predicate, so a lapsed hold never counts. Uses ix_seats_event_client_status.
        => await _context.Seats
            .AsNoTracking()
            .Where(seat =>
                seat.EventId == eventId
                && seat.HeldByClientId == clientId
                && seat.Status == SeatStatus.Held
                && seat.HoldExpiresAt > utcNow)
            .Select(seat => seat.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> FindExpiredHoldsAsync(
        DateTime utcNow,
        int limit,
        CancellationToken cancellationToken = default)
        // Oldest lapse first, so no row is starved. Uses the partial ix_seats_expiring_holds.
        => await _context.Seats
            .AsNoTracking()
            .Where(seat => seat.Status == SeatStatus.Held && seat.HoldExpiresAt <= utcNow)
            .OrderBy(seat => seat.HoldExpiresAt)
            .Take(limit)
            .Select(seat => seat.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task AddRangeAsync(
        IReadOnlyCollection<Seat> seats,
        CancellationToken cancellationToken = default)
    {
        await _context.Seats.AddRangeAsync(seats, cancellationToken).ConfigureAwait(false);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
