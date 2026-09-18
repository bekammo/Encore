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
    public async Task SaveAsync(Seat seat, CancellationToken cancellationToken = default)
    {
        // TODO: drain seat.DomainEvents into the outbox in this same transaction,
        // then ClearDomainEvents(). Waiting on the outbox table (Soundcheck).
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
    public Task<int> CountLiveHoldsAsync(
        Guid clientId,
        Guid eventId,
        Guid excludingSeatId,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
        // Expiry is part of the predicate rather than something filtered
        // afterwards, so a lapsed hold never counts even though its row still
        // says Held. Served by ix_seats_event_client_status; without that index
        // this is a sequential scan on every hold attempt during a flash sale,
        // which is the worst possible moment for one.
        => _context.Seats
            .Where(seat =>
                seat.EventId == eventId
                && seat.HeldByClientId == clientId
                && seat.Status == SeatStatus.Held
                && seat.HoldExpiresAt > utcNow
                && seat.Id != excludingSeatId)
            .CountAsync(cancellationToken);

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
