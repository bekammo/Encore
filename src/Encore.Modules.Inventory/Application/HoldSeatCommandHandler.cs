using Encore.Modules.Inventory.Contracts;
using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.Application;

/// <summary>
/// Holds seats for a client. Each <see cref="Seat"/> decides its own transition; this
/// handler owns only the per-client hold cap, which spans several rows.
/// </summary>
/// <remarks>
/// Each seat is answered on its own, and the holds that succeed are written in one
/// transaction. A client + event lock serialises the cap check: if another request by
/// the same client holds it, this one is refused; if the lock service is unavailable,
/// the attempt proceeds and the cap may be exceeded. A lost race is retried once.
/// </remarks>
public sealed class HoldSeatCommandHandler(
    ISeatRepository seats,
    IDistributedLock distributedLock,
    TimeProvider timeProvider)
{
    /// <summary>Seats one client may hold at one event at once.</summary>
    public static readonly int MaxHoldsPerClientPerEvent =
        SeatReservationLimits.MaxHoldsPerClientPerEvent;

    private readonly ISeatRepository _seats = seats;
    private readonly IDistributedLock _distributedLock = distributedLock;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <summary>Holds one seat. A batch of one.</summary>
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

    /// <summary>Holds every requested seat it can.</summary>
    /// <returns>One outcome per seat, in request order.</returns>
    public async Task<IReadOnlyList<HoldSeatResult>> HandleAsync(
        HoldSeatsCommand command,
        CancellationToken cancellationToken = default)
    {
        SeatBatch.EnsureValid(command.SeatIds);

        var clientResource = ClientHoldLock.Resource(command.ClientId, command.EventId);

        var clientLock = await _distributedLock
            .TryAcquireAsync(clientResource, ClientHoldLock.Ttl, cancellationToken)
            .ConfigureAwait(false);

        // Another request by this client is mid-count; letting both through could breach the cap.
        if (clientLock.Outcome is LockOutcome.HeldByAnother)
        {
            return [.. command.SeatIds.Select(_ => HoldSeatResult.ConcurrentRequestInFlight)];
        }

        try
        {
            var attempt = await AttemptAsync(command, cancellationToken).ConfigureAwait(false);

            if (!attempt.LostRace)
            {
                return attempt.Results;
            }

            // The retry's load discards the first attempt's changes.
            var retry = await AttemptAsync(command, cancellationToken).ConfigureAwait(false);

            if (retry.LostRace)
            {
                // Nothing else will: reload so holds that exist only in memory cannot reach a later save (011).
                await _seats.GetByIdsAsync(command.SeatIds, cancellationToken).ConfigureAwait(false);
            }

            return retry.Results;
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
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        // One read: the seats asked for, and the client's live holds for the cap.
        var loaded = await _seats
            .GetForHoldAsync(command.SeatIds, command.ClientId, command.EventId, utcNow, cancellationToken)
            .ConfigureAwait(false);

        // The event id is checked, never trusted.
        var seats = loaded.Seats
            .Where(seat => seat.EventId == command.EventId)
            .ToDictionary(seat => seat.Id);

        if (seats.Count is 0)
        {
            return new Attempt([.. command.SeatIds.Select(_ => HoldSeatResult.SeatNotFound)], LostRace: false);
        }

        var liveHolds = loaded.LiveHolds;
        var holding = liveHolds.Count;
        var results = new HoldSeatResult[command.SeatIds.Count];

        for (var i = 0; i < command.SeatIds.Count; i++)
        {
            if (!seats.TryGetValue(command.SeatIds[i], out var seat))
            {
                results[i] = HoldSeatResult.SeatNotFound;
                continue;
            }

            // Drop events left by a previous rejected attempt.
            seat.ClearDomainEvents();

            // Re-holding a seat you already hold is not a new hold, so the cap does not apply.
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

        // Every state change raises an event, so these are the seats that moved.
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
            // Nothing was written, so every seat this attempt moved lost the race.
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

        // Any other reason propagates: it would mean the aggregate's contract changed.
    }

    private sealed record Attempt(IReadOnlyList<HoldSeatResult> Results, bool LostRace);
}
