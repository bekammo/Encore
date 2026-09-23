using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Inventory.Adapters.Persistence;

/// <summary>
/// EF Core / Postgres implementation of <see cref="ISeatRepository"/>. This is
/// where the concurrency guarantee the port promises actually gets paid for —
/// the <c>xmin</c> token on the seat row turns every save into a conditional
/// UPDATE, and the loser of a race finds out here.
/// </summary>
/// <remarks>
/// The one thing this adapter must not do is let EF Core's vocabulary escape
/// through the port, so <see cref="DbUpdateConcurrencyException"/> is caught and
/// translated into <see cref="ConcurrentSeatModificationException"/>. The
/// original is kept as the inner exception: callers get a contract they can
/// depend on, diagnostics keep the detail.
/// </remarks>
public sealed class EfSeatRepository(InventoryDbContext context) : ISeatRepository
{
    private readonly InventoryDbContext _context = context;

    /// <inheritdoc />
    public async Task<Seat?> GetByIdAsync(Guid seatId, CancellationToken cancellationToken = default)
    {
        // EF's identity map would hand back the instance this context is already
        // tracking — stale concurrency token, previous attempt's mutations and all
        // — rather than what the database currently says. A caller reloading after
        // losing a race would then re-attempt against exactly the state that just
        // lost, and the retry would be theatre. Force a real read instead.
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

        // Reload detaches the entry when the row has gone, in which case the
        // instance it still points at describes a seat that no longer exists.
        return tracked.State is EntityState.Detached ? null : tracked.Entity;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Seat>> GetByIdsAsync(
        IReadOnlyCollection<Guid> seatIds,
        CancellationToken cancellationToken = default)
    {
        // Detached and read again in one query, rather than reloaded one entry at
        // a time as GetByIdAsync does: a retried four-seat batch would otherwise
        // pay four round trips to learn what one can tell it. Detaching also
        // discards whatever a refused or rejected attempt did to these instances,
        // so nothing it changed can reach a later save.
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
        // The outbox drain is not this method's: InventoryDbContext's SaveChanges
        // override owns it, for the reasons in DECISIONS 044. It runs underneath
        // this call, so the seat's events and the seat's new state reach Postgres
        // in one transaction — including on the AddRangeAsync path below, which a
        // drain written here would have missed.
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
        // One SaveChanges is one transaction, and Npgsql sends its statements as
        // one batch: every seat's conditional UPDATE and every outbox INSERT land
        // together or not at all, in a single round trip.
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
        // Expiry is part of the predicate rather than something filtered
        // afterwards, so a lapsed hold never counts even though its row still
        // says Held. Served by ix_seats_event_client_status; without that index
        // this is a sequential scan on every hold attempt during a flash sale,
        // which is the worst possible moment for one.
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
        // AsNoTracking and ids only: these rows are candidates for another
        // context to load, and tracking them here would populate an identity map
        // that GetByIdAsync then has to reload past.
        //
        // Ordered by the oldest lapse, so a backlog is worked through in the
        // order it accumulated and a row cannot be starved by newer arrivals
        // between one batch and the next.
        //
        // Served by ix_seats_expiring_holds, the partial index on HoldExpiresAt
        // over held rows that 068 added for exactly this predicate and ordering.
        // The limit stays regardless: a backlog is worked through in batches, not
        // materialised whole.
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
        // One SaveChangesAsync, so EF wraps the whole batch in a single
        // transaction and a half-written seat map is not reachable.
        await _context.Seats.AddRangeAsync(seats, cancellationToken).ConfigureAwait(false);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
