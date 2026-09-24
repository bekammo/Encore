using System.Linq.Expressions;
using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Inventory.Adapters.Persistence;

/// <summary>
/// EF's concurrency exception never crosses the port: it is translated, and kept as the inner
/// exception (002).
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
        // Detached first: a query would hand back the tracked instance, unsaved changes and all.
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

        // A requested seat the client already holds comes back from both halves of the UNION ALL.
        var rows = (await _context.Seats
                .Where(seat => seatIds.Contains(seat.Id))
                .Concat(_context.Seats.Where(liveHold))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .DistinctBy(seat => seat.Id)
            .ToList();

        var isLiveHold = liveHold.Compile();
        var liveHolds = rows.Where(isLiveHold).Select(seat => seat.Id).ToList();

        // Untracked, so no later save can write a seat the cap only counted.
        foreach (var counted in rows.Where(seat => !seatIds.Contains(seat.Id) && !alreadyTracked.Contains(seat.Id)))
        {
            _context.Entry(counted).State = EntityState.Detached;
        }

        return new SeatsForHold([.. rows.Where(seat => seatIds.Contains(seat.Id))], liveHolds);
    }

    /// <inheritdoc />
    public async Task SaveAsync(Seat seat, CancellationToken cancellationToken = default)
    {
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
    public async Task<IReadOnlyList<Guid>> FindExpiredHoldsAsync(
        DateTime utcNow,
        int limit,
        CancellationToken cancellationToken = default)
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
        _context.Seats.AddRange(seats);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // One definition for the query and the in-memory check. Expiry is in it, so a lapsed Held
    // row is never a live hold (006).
    private static Expression<Func<Seat, bool>> LiveHoldOf(Guid clientId, Guid eventId, DateTime utcNow) =>
        seat => seat.EventId == eventId
            && seat.HeldByClientId == clientId
            && seat.Status == SeatStatus.Held
            && seat.HoldExpiresAt > utcNow;

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
}
