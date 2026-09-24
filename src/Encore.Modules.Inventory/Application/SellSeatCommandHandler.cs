using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.Application;

public sealed class SellSeatCommandHandler(
    ISeatRepository seats,
    TimeProvider timeProvider)
{
    private readonly ISeatRepository _seats = seats;
    private readonly TimeProvider _timeProvider = timeProvider;

    public async Task<SellSeatOutcome> HandleAsync(
        SellSeatCommand command,
        CancellationToken cancellationToken = default)
    {
        var result = await HandleAsync(
                new SellSeatsCommand(command.EventId, [command.SeatId], command.ClientId),
                cancellationToken)
            .ConfigureAwait(false);

        return result.AllSold ? SellSeatOutcome.Sold : result.Refusals[0].Outcome;
    }

    public async Task<SellSeatsResult> HandleAsync(
        SellSeatsCommand command,
        CancellationToken cancellationToken = default)
    {
        SeatBatch.EnsureValid(command.SeatIds);

        var attempt = await AttemptAsync(command, cancellationToken).ConfigureAwait(false);

        if (!attempt.LostRace)
        {
            return attempt.Result;
        }

        var retry = await AttemptAsync(command, cancellationToken).ConfigureAwait(false);

        if (retry.LostRace)
        {
            // Reloads only to discard the sales in memory, so no later save writes them (011).
            await _seats.GetByIdsAsync(command.SeatIds, cancellationToken).ConfigureAwait(false);
        }

        return retry.Result;
    }

    private async Task<Attempt> AttemptAsync(
        SellSeatsCommand command,
        CancellationToken cancellationToken)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        var loaded = await _seats.GetByIdsAsync(command.SeatIds, cancellationToken).ConfigureAwait(false);

        var seats = loaded
            .Where(seat => seat.EventId == command.EventId)
            .ToDictionary(seat => seat.Id);

        var refusals = new List<SeatSaleRefusal>();

        foreach (var seatId in command.SeatIds)
        {
            if (!seats.TryGetValue(seatId, out var seat))
            {
                refusals.Add(new SeatSaleRefusal(seatId, SellSeatOutcome.SeatNotFound));
                continue;
            }

            // Drop events left by a previous rejected attempt.
            seat.ClearDomainEvents();

            if (TrySell(seat, command.ClientId, utcNow) is { } refusal)
            {
                refusals.Add(new SeatSaleRefusal(seatId, refusal));
            }
        }

        // Every state change raises an event, so these are the seats that moved.
        var sold = seats.Values.Where(seat => seat.DomainEvents.Count > 0).ToList();

        if (refusals.Count > 0)
        {
            // A refused sale reloads, so seats sold in memory cannot reach a later save (011).
            if (sold.Count > 0)
            {
                await _seats.GetByIdsAsync(command.SeatIds, cancellationToken).ConfigureAwait(false);
            }

            return new Attempt(new SellSeatsResult(refusals), LostRace: false);
        }

        if (sold.Count is 0)
        {
            return new Attempt(SellSeatsResult.Sold, LostRace: false);
        }

        try
        {
            await _seats.SaveAsync(sold, cancellationToken).ConfigureAwait(false);

            return new Attempt(SellSeatsResult.Sold, LostRace: false);
        }
        catch (ConcurrentSeatModificationException ex)
        {
            return new Attempt(
                new SellSeatsResult([new SeatSaleRefusal(ex.SeatId, SellSeatOutcome.LostRace)]),
                LostRace: true);
        }
    }

    // Null when the seat sold now or was already sold to this client.
    private static SellSeatOutcome? TrySell(Seat seat, Guid clientId, DateTime utcNow)
    {
        try
        {
            seat.Sell(clientId, utcNow);

            return null;
        }
        catch (SeatTransitionException ex) when (ex.Reason is SeatTransitionReason.SeatAlreadySold)
        {
            return seat.HeldByClientId == clientId ? null : SellSeatOutcome.AlreadySold;
        }
        catch (SeatTransitionException ex) when (ex.Reason is SeatTransitionReason.NotTheHolder)
        {
            return SellSeatOutcome.NotTheHolder;
        }
        catch (SeatTransitionException ex) when (ex.Reason is SeatTransitionReason.HoldExpired)
        {
            return SellSeatOutcome.HoldExpired;
        }
        catch (SeatTransitionException ex) when (ex.Reason is SeatTransitionReason.NoActiveHold)
        {
            return SellSeatOutcome.NoActiveHold;
        }
    }

    private sealed record Attempt(SellSeatsResult Result, bool LostRace);
}
