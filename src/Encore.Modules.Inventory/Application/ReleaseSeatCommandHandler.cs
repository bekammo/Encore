using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.Application;

/// <summary>
/// The "I don't want this seat after all" use case: hand a held seat back to the
/// pool. Sequences the ports the same way the other seat writes do — lock, load,
/// let the domain decide, persist.
/// </summary>
/// <remarks>
/// <para>
/// <b>One lock, and only as an optimisation.</b> Releasing touches a single row,
/// so the concurrency token settles any race and a contended or unreachable lock
/// changes nothing but throughput. There is no cap to protect here: releasing
/// gives capacity back rather than consuming it, so unlike holding there is
/// nothing a missed lock could let a client cheat.
/// </para>
/// <para>
/// <b>Releasing a seat you are not holding is a success.</b> The caller wanted
/// not to be holding this seat, and they are not — whether their hold lapsed
/// while the request was in flight, or the request is a duplicate of one that
/// already worked. Refusing would turn a retry into an error for a state that
/// already holds, which is the same reasoning that makes a re-hold and a
/// re-purchase idempotent.
/// </para>
/// </remarks>
public sealed class ReleaseSeatCommandHandler(
    ISeatRepository seats,
    IDistributedLock distributedLock,
    TimeProvider timeProvider)
{
    private readonly ISeatRepository _seats = seats;
    private readonly IDistributedLock _distributedLock = distributedLock;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <summary>
    /// Attempts to release <see cref="ReleaseSeatCommand.SeatId"/> on behalf of
    /// <see cref="ReleaseSeatCommand.ClientId"/>.
    /// </summary>
    /// <returns>The outcome. Refusals are returned, not thrown.</returns>
    public async Task<ReleaseSeatResult> HandleAsync(
        ReleaseSeatCommand command,
        CancellationToken cancellationToken = default)
    {
        var resource = SeatLocks.ForSeat(command.SeatId);

        var seatLock = await _distributedLock
            .TryAcquireAsync(resource, SeatLocks.Ttl, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var result = await AttemptAsync(command, cancellationToken).ConfigureAwait(false);

            return result.Outcome is ReleaseSeatOutcome.LostRace
                ? await AttemptAsync(command, cancellationToken).ConfigureAwait(false)
                : result;
        }
        finally
        {
            await _distributedLock.ReleaseIfHeldAsync(resource, seatLock).ConfigureAwait(false);
        }
    }

    private async Task<ReleaseSeatResult> AttemptAsync(
        ReleaseSeatCommand command,
        CancellationToken cancellationToken)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        var seat = await _seats.GetByIdAsync(command.SeatId, cancellationToken).ConfigureAwait(false);

        if (seat is null || seat.EventId != command.EventId)
        {
            return ReleaseSeatResult.SeatNotFound;
        }

        // A rejected attempt may have left events on this instance describing a
        // release that never happened.
        seat.ClearDomainEvents();

        try
        {
            seat.Release(command.ClientId, utcNow);
            await _seats.SaveAsync(seat, cancellationToken).ConfigureAwait(false);

            return ReleaseSeatResult.Released;
        }
        catch (SeatTransitionException ex) when (ex.Reason is SeatTransitionReason.SeatAlreadySold)
        {
            return ReleaseSeatResult.AlreadySold;
        }
        catch (SeatTransitionException ex) when (ex.Reason is SeatTransitionReason.NotTheHolder)
        {
            return ReleaseSeatResult.NotTheHolder;
        }
        catch (ConcurrentSeatModificationException)
        {
            return ReleaseSeatResult.LostRace;
        }

        // Release() refuses for exactly the two reasons handled above. A third
        // would mean the aggregate's contract moved without this handler being
        // told, and it is left to propagate rather than be swallowed.
    }

}
