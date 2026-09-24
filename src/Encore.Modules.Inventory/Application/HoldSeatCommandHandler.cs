using Encore.Modules.Inventory.Contracts;
using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.Application;

public sealed class HoldSeatCommandHandler(
    ISeatRepository seats,
    IDistributedLock distributedLock,
    TimeProvider timeProvider)
{
    private readonly ISeatRepository _seats = seats;
    private readonly IDistributedLock _distributedLock = distributedLock;
    private readonly TimeProvider _timeProvider = timeProvider;

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

    public async Task<IReadOnlyList<HoldSeatResult>> HandleAsync(
        HoldSeatsCommand command,
        CancellationToken cancellationToken = default)
    {
        SeatBatch.EnsureValid(command.SeatIds);

        var clientResource = ClientHoldLock.Resource(command.ClientId, command.EventId);

        var clientLock = await _distributedLock
            .TryAcquireAsync(clientResource, ClientHoldLock.Ttl, cancellationToken)
            .ConfigureAwait(false);

        // Unavailable falls through: without the lock, the cap may be exceeded (005).
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

            var retry = await AttemptAsync(command, cancellationToken).ConfigureAwait(false);

            if (retry.LostRace)
            {
                // Reloads only to discard the holds in memory, so no later save writes them (011).
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

        var loaded = await _seats
            .GetForHoldAsync(command.SeatIds, command.ClientId, command.EventId, utcNow, cancellationToken)
            .ConfigureAwait(false);

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

            var alreadyTheirs = liveHolds.Contains(seat.Id);

            if (!alreadyTheirs && holding >= SeatReservationLimits.MaxHoldsPerClientPerEvent)
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
    }

    private sealed record Attempt(IReadOnlyList<HoldSeatResult> Results, bool LostRace);
}
