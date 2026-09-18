using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.Application;

/// <summary>
/// The "put this seat in my basket" use case. Thin by design: read the clock,
/// take the locks, load the aggregate, let the domain decide whether the
/// transition is legal, persist. Every rule about the seat itself lives in
/// <see cref="Domain.Seat"/>; the one rule this class owns is the per-client
/// hold cap, which spans several rows and so cannot live in an aggregate whose
/// consistency boundary is one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The Redis locks are an optimisation and are treated like one.</b> Failing
/// to acquire either is not an error and does not abort the attempt — the
/// handler carries on to Postgres, where the concurrency token settles the race
/// properly. Correctness therefore survives Redis being gone entirely; what is
/// lost is the throughput saved by keeping the losers off the database, and the
/// hold cap's reliability (see below).
/// </para>
/// <para>
/// <b>Two locks, always in the same order: client+event, then seat.</b> The
/// client lock has to span the cap count *and* the write, or the check-then-act
/// gap it exists to close stays open. That forces it outside the seat lock. The
/// order being fixed and identical on every path is what makes deadlock
/// impossible — a cycle needs two callers disagreeing about it — so if a third
/// use case ever takes both, it takes them this way round.
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
    /// How many seats one client may hold at one event at once
    /// (<c>DECISIONS.md</c> 006).
    /// </summary>
    /// <remarks>
    /// A constant rather than configuration, deliberately. Per-event caps — a
    /// small venue wanting a tighter limit — would be a different rule with a
    /// different home, and leaving this settable invites it being changed
    /// without anyone arguing for the new number.
    /// </remarks>
    public const int MaxHoldsPerClientPerEvent = 4;

    /// <summary>
    /// How long a lock survives if it is never released. Sized to one write
    /// attempt, never to the business-level hold window: it is a safety net for
    /// a process that died mid-write, not a booking.
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
        var clientResource = ClientResourceFor(command.ClientId, command.EventId);
        var seatResource = SeatResourceFor(command.SeatId);

        // Outer: serialises this client against themselves across the several
        // seats the cap counts. Rarely contended, so rarely missed.
        var clientToken = await _distributedLock
            .TryAcquireAsync(clientResource, LockTtl, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            // Inner: the hot one during a flash sale. A null token means somebody
            // else has it, which is not a failure condition — proceed anyway and
            // let optimistic concurrency decide.
            var seatToken = await _distributedLock
                .TryAcquireAsync(seatResource, LockTtl, cancellationToken)
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
                await ReleaseAsync(seatResource, seatToken, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await ReleaseAsync(clientResource, clientToken, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<HoldSeatResult> AttemptAsync(
        HoldSeatCommand command,
        CancellationToken cancellationToken)
    {
        // One reading per attempt, shared by the cap count and the transition, so
        // the two cannot disagree about when "now" is. Re-read on a retry, because
        // by then it genuinely is later.
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        var seat = await _seats.GetByIdAsync(command.SeatId, cancellationToken).ConfigureAwait(false);

        // The command's event id is checked, never trusted: it decides which
        // holds get counted and which lock is taken, so a wrong one would apply
        // the cap to the wrong event's basket.
        if (seat is null || seat.EventId != command.EventId)
        {
            return HoldSeatResult.SeatNotFound;
        }

        // A previous attempt may have raised events onto this instance before its
        // write was rejected. Those describe something that never happened, so
        // they must not survive into the attempt that does.
        seat.ClearDomainEvents();

        // Counted inside the attempt rather than once up front: after losing a
        // race the world has moved, and a cap decision made against the old world
        // is a decision about a state that no longer exists.
        var otherHolds = await _seats
            .CountLiveHoldsAsync(command.ClientId, command.EventId, command.SeatId, utcNow, cancellationToken)
            .ConfigureAwait(false);

        if (otherHolds >= MaxHoldsPerClientPerEvent)
        {
            return HoldSeatResult.HoldCapReached;
        }

        try
        {
            seat.Hold(command.ClientId, utcNow);
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

    private async Task ReleaseAsync(string resource, string? token, CancellationToken cancellationToken)
    {
        if (token is not null)
        {
            await _distributedLock.ReleaseAsync(resource, token, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string SeatResourceFor(Guid seatId) => $"seat:{seatId}";

    private static string ClientResourceFor(Guid clientId, Guid eventId) =>
        $"client:{clientId}:event:{eventId}";
}
