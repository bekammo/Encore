using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.Application;

/// <summary>
/// Turns a client's live holds into sales: every seat or none, in one transaction,
/// because a sale cannot be taken back.
/// </summary>
/// <remarks>
/// Buying a seat the client already bought is a success, not a refusal. No lock: each
/// seat's <c>xmin</c> settles its own race. A lost race is retried once.
/// </remarks>
public sealed class SellSeatCommandHandler(
    ISeatRepository seats,
    TimeProvider timeProvider)
{
    private readonly ISeatRepository _seats = seats;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <summary>Sells one seat. A batch of one.</summary>
    public async Task<SellSeatResult> HandleAsync(
        SellSeatCommand command,
        CancellationToken cancellationToken = default)
    {
        var result = await HandleAsync(
                new SellSeatsCommand(command.EventId, [command.SeatId], command.ClientId),
                cancellationToken)
            .ConfigureAwait(false);

        return result.AllSold ? SellSeatResult.Sold : new SellSeatResult(result.Refusals[0].Outcome);
    }

    /// <summary>Sells every requested seat, or none.</summary>
    /// <returns>Sold, or every seat's reason for refusing.</returns>
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

        // The retry's load discards the first attempt's changes.
        var retry = await AttemptAsync(command, cancellationToken).ConfigureAwait(false);

        if (retry.LostRace)
        {
            // Nothing else will: reload so the seats that read Sold in memory cannot reach a later save.
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

        // The event id is checked, never trusted.
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
            // Reload so the seats that read Sold in memory cannot reach a later save.
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

    /// <summary>Asks one seat to sell; returns why it would not, or null if it did.</summary>
    private static SellSeatOutcome? TrySell(Seat seat, Guid clientId, DateTime utcNow)
    {
        try
        {
            seat.Sell(clientId, utcNow);

            return null;
        }
        catch (SeatTransitionException ex) when (ex.Reason is SeatTransitionReason.SeatAlreadySold)
        {
            // Already sold to this client means a retried purchase, not a failure.
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

        // Any other reason propagates: it would mean the aggregate's contract changed.
    }

    private sealed record Attempt(SellSeatsResult Result, bool LostRace);
}
