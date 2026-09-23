using Encore.Modules.Inventory.Contracts;
using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.Application;

/// <summary>
/// The "put these seats in my basket" use case. Thin by design: read the clock,
/// take the client lock, load the aggregates, let each one decide whether its
/// transition is legal, persist them together. Every rule about a seat itself
/// lives in <see cref="Seat"/>; the one rule this class owns is the per-client
/// hold cap, which spans several rows and so cannot live in an aggregate whose
/// consistency boundary is one.
/// </summary>
/// <remarks>
/// <para>
/// <b>One batch, one round trip, and every seat answered on its own.</b> A seat
/// that is refused does not cost the client the seats that could be held — that
/// is 023, and the batch keeps it. What changed in 076 is only the cost: the
/// holds that succeed are written in one transaction instead of one each, and a
/// single-seat request is simply a batch of one.
/// </para>
/// <para>
/// <b>One lock, on the client and event, and it is not optional.</b> It has to
/// span the cap check <i>and</i> the write, or the check-then-act gap it exists to
/// close stays open. Contention is refused rather than waved through, because
/// nothing behind the lock enforces the cap — concurrent requests by one client
/// are precisely when it is contended. An <i>unavailable</i> lock is different and
/// the attempt proceeds: that is 006's asymmetry, a breached cap being a refund
/// email while refusing every hold because Redis blinked is an outage (010).
/// </para>
/// <para>
/// There is no seat lock. Every handler used to take one and proceed whatever it
/// answered, so it excluded nobody; <c>xmin</c> settles a race for a seat, and
/// always did (076).
/// </para>
/// <para>
/// <b>A lost race is retried exactly once.</b> Losing means somebody else wrote
/// one of these rows first, so it almost certainly now reads <c>Held</c> by them —
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
    /// <para>
    /// Fixed rather than configuration, deliberately. Per-event caps — a small
    /// venue wanting a tighter limit — would be a different rule with a
    /// different home, and leaving this settable invites it being changed
    /// without anyone arguing for the new number.
    /// </para>
    /// <para>
    /// The value itself now lives on <see cref="SeatReservationLimits"/>, so a
    /// caller outside this module can size a request before sending it instead
    /// of learning the limit by being refused. This stays as the name the
    /// handler and its tests already use; it is a forwarder, not a second copy.
    /// </para>
    /// </remarks>
    public static readonly int MaxHoldsPerClientPerEvent =
        SeatReservationLimits.MaxHoldsPerClientPerEvent;

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
        var results = await HandleAsync(
                new HoldSeatsCommand(command.EventId, [command.SeatId], command.ClientId),
                cancellationToken)
            .ConfigureAwait(false);

        return results[0];
    }

    /// <summary>
    /// Attempts to hold every seat in <see cref="HoldSeatsCommand.SeatIds"/> for
    /// <see cref="HoldSeatsCommand.ClientId"/>.
    /// </summary>
    /// <returns>One outcome per seat, in the order the seats were asked for.</returns>
    public async Task<IReadOnlyList<HoldSeatResult>> HandleAsync(
        HoldSeatsCommand command,
        CancellationToken cancellationToken = default)
    {
        SeatBatch.EnsureValid(command.SeatIds);

        var clientResource = SeatLocks.ForClient(command.ClientId, command.EventId);

        var clientLock = await _distributedLock
            .TryAcquireAsync(clientResource, SeatLocks.Ttl, cancellationToken)
            .ConfigureAwait(false);

        // Contended means another request by this same client is mid-count for
        // this event. Proceeding would count a world that is about to change and
        // let both requests past a cap that only one of them fits under.
        if (clientLock.Outcome is LockOutcome.HeldByAnother)
        {
            return [.. command.SeatIds.Select(_ => HoldSeatResult.ConcurrentRequestInFlight)];
        }

        try
        {
            var attempt = await AttemptAsync(command, cancellationToken).ConfigureAwait(false);

            return attempt.LostRace
                ? (await AttemptAsync(command, cancellationToken).ConfigureAwait(false)).Results
                : attempt.Results;
        }
        finally
        {
            await _distributedLock
                .ReleaseIfHeldAsync(clientResource, clientLock)
                .ConfigureAwait(false);
        }
    }

    private async Task<Attempt> AttemptAsync(
        HoldSeatsCommand command,
        CancellationToken cancellationToken)
    {
        // One reading per attempt, shared by the cap and every transition, so they
        // cannot disagree about when "now" is. Re-read on a retry, because by then
        // it genuinely is later.
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        var loaded = await _seats.GetByIdsAsync(command.SeatIds, cancellationToken).ConfigureAwait(false);

        // The command's event id is checked, never trusted: it decides which
        // holds get counted and which lock is taken, so a wrong one would apply
        // the cap to the wrong event's basket.
        var seats = loaded
            .Where(seat => seat.EventId == command.EventId)
            .ToDictionary(seat => seat.Id);

        if (seats.Count is 0)
        {
            return new Attempt([.. command.SeatIds.Select(_ => HoldSeatResult.SeatNotFound)], LostRace: false);
        }

        // Counted inside the attempt rather than once up front: after losing a
        // race the world has moved, and a cap decision made against the old world
        // is a decision about a state that no longer exists.
        var liveHolds = await _seats
            .FindLiveHoldsAsync(command.ClientId, command.EventId, utcNow, cancellationToken)
            .ConfigureAwait(false);

        var holding = liveHolds.Count;
        var results = new HoldSeatResult[command.SeatIds.Count];

        for (var i = 0; i < command.SeatIds.Count; i++)
        {
            if (!seats.TryGetValue(command.SeatIds[i], out var seat))
            {
                results[i] = HoldSeatResult.SeatNotFound;
                continue;
            }

            // A previous attempt may have raised events onto this instance before
            // its write was rejected. Those describe something that never
            // happened, so they must not survive into the attempt that does.
            seat.ClearDomainEvents();

            // A seat this client already holds is not a new hold, so the cap has
            // nothing to say about it: re-asking is the idempotent path (007), and
            // refusing it at the cap would refuse the client their own seat.
            var alreadyTheirs = liveHolds.Contains(seat.Id);

            if (!alreadyTheirs && holding >= MaxHoldsPerClientPerEvent)
            {
                results[i] = HoldSeatResult.HoldCapReached;
                continue;
            }

            results[i] = TryHold(seat, command.ClientId, utcNow);

            if (!alreadyTheirs && results[i].Outcome is HoldSeatOutcome.Held)
            {
                holding++;
            }
        }

        // Every state change a seat makes raises an event, so these are the seats
        // this attempt actually moved. A batch of refusals and idempotent re-holds
        // has nothing to write.
        var changed = seats.Values.Where(seat => seat.DomainEvents.Count > 0).ToList();

        if (changed.Count is 0)
        {
            return new Attempt(results, LostRace: false);
        }

        try
        {
            await _seats.SaveAsync(changed, cancellationToken).ConfigureAwait(false);

            return new Attempt(results, LostRace: false);
        }
        catch (ConcurrentSeatModificationException)
        {
            // Nothing was written, so no seat this attempt moved is held.
            var moved = changed.Select(seat => seat.Id).ToHashSet();

            return new Attempt(
                [.. command.SeatIds.Select((seatId, i) =>
                    moved.Contains(seatId) ? HoldSeatResult.LostRace : results[i])],
                LostRace: true);
        }
    }

    private static HoldSeatResult TryHold(Seat seat, Guid clientId, DateTime utcNow)
    {
        try
        {
            seat.Hold(clientId, utcNow);

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

        // Any other SeatTransitionReason is deliberately left to propagate. Hold()
        // refuses for exactly the two reasons handled above; a third would mean the
        // aggregate's contract moved without this handler being told, and mapping
        // it to some catch-all response here would hide that until it mattered.
    }

    private sealed record Attempt(IReadOnlyList<HoldSeatResult> Results, bool LostRace);
}
