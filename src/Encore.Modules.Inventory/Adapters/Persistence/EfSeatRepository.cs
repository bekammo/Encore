using System.Linq.Expressions;
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
    /// <remarks>A batch of one, so a stale seat is discarded exactly as a batch discards it.</remarks>
    public async Task<Seat?> GetByIdAsync(Guid seatId, CancellationToken cancellationToken = default) =>
        (await GetByIdsAsync([seatId], cancellationToken).ConfigureAwait(false)).SingleOrDefault();

    /// <inheritdoc />
    public async Task<IReadOnlyList<Seat>> GetByIdsAsync(
        IReadOnlyCollection<Guid> seatIds,
        CancellationToken cancellationToken = default)
    {
        // Detach and re-read in one query, discarding whatever a failed attempt changed.
        Detach(seatIds);

        return await _context.Seats
            .Where(seat => seatIds.Contains(seat.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SeatsForHold> GetForHoldAsync(
        IReadOnlyCollection<Guid> seatIds,
        Guid clientId,
        Guid eventId,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        Detach(seatIds);

        // Whatever this scope already tracks stays as it was; only what this read adds is let go.
        var alreadyTracked = _context.ChangeTracker
            .Entries<Seat>()
            .Select(entry => entry.Entity.Id)
            .ToHashSet();

        var liveHold = LiveHoldOf(clientId, eventId, utcNow);

        // One round trip, a UNION ALL: the requested seats by primary key, and the client's
        // live holds by ix_seats_event_client_status. A seat can come back from both halves.
        var rows = (await _context.Seats
                .Where(seat => seatIds.Contains(seat.Id))
                .Concat(_context.Seats.Where(liveHold))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .DistinctBy(seat => seat.Id)
            .ToList();

        var isLiveHold = liveHold.Compile();
        var liveHolds = rows.Where(isLiveHold).Select(seat => seat.Id).ToList();

        // The cap's other seats are only counted. Untracked, so no later save can write them.
        foreach (var counted in rows.Where(seat => !seatIds.Contains(seat.Id) && !alreadyTracked.Contains(seat.Id)))
        {
            _context.Entry(counted).State = EntityState.Detached;
        }

        return new SeatsForHold([.. rows.Where(seat => seatIds.Contains(seat.Id))], liveHolds);
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

    /// <summary>
    /// A live hold by this client at this event, as the cap counts it. Expiry is in the
    /// predicate, so a lapsed hold never counts. One definition, for the query and for memory.
    /// </summary>
    private static Expression<Func<Seat, bool>> LiveHoldOf(Guid clientId, Guid eventId, DateTime utcNow) =>
        seat => seat.EventId == eventId
            && seat.HeldByClientId == clientId
            && seat.Status == SeatStatus.Held
            && seat.HoldExpiresAt > utcNow;

    /// <summary>Stops tracking these seats, so the next read gets them as the database has them.</summary>
    private void Detach(IReadOnlyCollection<Guid> seatIds)
    {
        var tracked = _context.ChangeTracker
            .Entries<Seat>()
            .Where(entry => seatIds.Contains(entry.Entity.Id))
            .ToList();

        foreach (var entry in tracked)
        {
            entry.State = EntityState.Detached;
        }
    }

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
