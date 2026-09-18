using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.Application;

/// <summary>
/// The "put this seat in my basket" use case. Thin by design: read the clock,
/// take the lock, load the aggregate, let the domain decide whether the
/// transition is legal, persist. Every rule it appears to enforce actually
/// lives in <see cref="Domain.Seat"/>; this class only sequences the ports.
/// </summary>
/// <remarks>
/// <para>
/// <b>The Redis lock is an optimisation and is treated like one.</b> Failing to
/// acquire it is not an error and does not abort the attempt — the handler
/// carries on to Postgres, where the concurrency token settles the race properly.
/// Correctness therefore survives Redis being gone entirely; all that is lost is
/// the throughput saved by keeping the losers off the database.
/// </para>
/// <para>
/// <b>A lost race is retried exactly once.</b> Losing means somebody else wrote
/// the row first, so the seat almost certainly now reads <c>Held</c> by them —
/// reloading and re-asking turns a bare "you lost a race", which tells a customer
/// nothing, into the accurate "somebody already has it". The retry is bounded at
/// one because a second loss means genuine sustained contention, and retrying
/// harder at exactly the moment the system is busiest is how a thundering herd
/// gets worse instead of better.
/// </para>
/// </remarks>
public sealed class HoldSeatCommandHandler(
    ISeatRepository seats,
    IDistributedLock distributedLock,
    TimeProvider timeProvider)
{
    /// <summary>
    /// How long the per-seat lock survives if it is never released. Sized to one
    /// write attempt, never to the business-level hold window: it is a safety net
    /// for a process that died mid-write, not a booking.
    /// </summary>
    private static readonly TimeSpan LockTtl = TimeSpan.FromSeconds(5);

    private readonly ISeatRepository _seats = seats;
    private readonly IDistributedLock _distributedLock = distributedLock;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <summary>
    /// Attempts to hold <see cref="HoldSeatCommand.SeatId"/> for
    /// <see cref="HoldSeatCommand.ClientId"/>.
    /// </summary>
    /// <returns>
    /// The outcome. Refusals are returned, not thrown — see
    /// <see cref="HoldSeatOutcome"/> for why.
    /// </returns>
    public async Task<HoldSeatResult> HandleAsync(
        HoldSeatCommand command,
        CancellationToken cancellationToken = default)
    {
        var resource = ResourceFor(command.SeatId);

        // A null token means somebody else holds the lock. That is not a failure
        // condition here: proceed anyway and let optimistic concurrency decide.
        var token = await _distributedLock
            .TryAcquireAsync(resource, LockTtl, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var result = await AttemptAsync(command, cancellationToken).ConfigureAwait(false);

            return result.Outcome is HoldSeatOutcome.LostRace
                ? await AttemptAsync(command, cancellationToken).ConfigureAwait(false)
                : result;
        }
        finally
        {
            if (token is not null)
            {
                await _distributedLock
                    .ReleaseAsync(resource, token, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task<HoldSeatResult> AttemptAsync(
        HoldSeatCommand command,
        CancellationToken cancellationToken)
    {
        var seat = await _seats.GetByIdAsync(command.SeatId, cancellationToken).ConfigureAwait(false);

        if (seat is null)
        {
            return HoldSeatResult.SeatNotFound;
        }

        // A previous attempt may have raised events onto this instance before its
        // write was rejected. Those describe something that never happened, so
        // they must not survive into the attempt that does.
        seat.ClearDomainEvents();

        try
        {
            seat.Hold(command.ClientId, _timeProvider.GetUtcNow().UtcDateTime);
            await _seats.SaveAsync(seat, cancellationToken).ConfigureAwait(false);

            return HoldSeatResult.Held(seat.HoldExpiresAt!.Value);
        }
        catch (SeatTransitionException ex) when (ex.Reason is SeatTransitionReason.SeatAlreadyHeld)
        {
            return HoldSeatResult.AlreadyHeld;
        }
        catch (SeatTransitionException ex) when (ex.Reason is SeatTransitionReason.SeatAlreadySold)
        {
            return HoldSeatResult.AlreadySold;
        }
        catch (ConcurrentSeatModificationException)
        {
            return HoldSeatResult.LostRace;
        }

        // Any other SeatTransitionReason is deliberately left to propagate. Hold()
        // refuses for exactly the two reasons handled above; a third would mean the
        // aggregate's contract moved without this handler being told, and mapping
        // it to some catch-all response here would hide that until it mattered.
    }

    private static string ResourceFor(Guid seatId) => $"seat:{seatId}";
}
