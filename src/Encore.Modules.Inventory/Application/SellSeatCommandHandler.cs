using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.Application;

/// <summary>
/// The "complete my checkout" use case: turn this client's live holds into
/// confirmed sales — every one of them, or none. Load, let each aggregate decide,
/// persist them together.
/// </summary>
/// <remarks>
/// <para>
/// This is the path where getting it wrong is most expensive. A cap breach is a
/// refund email; two people holding a receipt for the same seat is one of them
/// standing outside a sold-out venue. The invariant is carried by the same
/// optimistic-concurrency token as every other transition — <c>Sold</c> is
/// terminal in the aggregate, and the conditional write means only one attempt
/// can ever reach it.
/// </para>
/// <para>
/// <b>All or none, because a sale cannot be taken back.</b> Sold one at a time, an
/// order whose fourth hold had lapsed left three seats sold to nobody who paid for
/// them (028 named that case and left it for a human). Now every seat's transition
/// is decided first, and only a batch with no refusal is written, in one
/// transaction. Each seat still enforces its own rules; the transaction adds
/// atomicity and no rule of its own. See 076.
/// </para>
/// <para>
/// <b>Selling a seat this client already bought is a success, not a refusal.</b>
/// <see cref="Seat"/> keeps <c>HeldByClientId</c> when it sells, precisely so the
/// row can still answer "who owns this", and that is what makes the distinction
/// possible here. A customer whose response was lost, or who double-submitted the
/// checkout form, is asking for a state that already holds. It raises no second
/// <see cref="Domain.Events.SeatSold"/>, because nothing happened.
/// </para>
/// <para>
/// No lock. Nothing here spans rows that a row's token cannot guard: each seat's
/// <c>xmin</c> settles its own race, and the transaction makes them land together.
/// </para>
/// </remarks>
public sealed class SellSeatCommandHandler(
    ISeatRepository seats,
    TimeProvider timeProvider)
{
    private readonly ISeatRepository _seats = seats;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <summary>
    /// Attempts to sell <see cref="SellSeatCommand.SeatId"/> to
    /// <see cref="SellSeatCommand.ClientId"/>.
    /// </summary>
    /// <returns>The outcome. Refusals are returned, not thrown.</returns>
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

    /// <summary>
    /// Attempts to sell every seat in <see cref="SellSeatsCommand.SeatIds"/> to
    /// <see cref="SellSeatsCommand.ClientId"/>, all of them or none.
    /// </summary>
    /// <returns>Sold, or every seat's reason for refusing.</returns>
    public async Task<SellSeatsResult> HandleAsync(
        SellSeatsCommand command,
        CancellationToken cancellationToken = default)
    {
        SeatBatch.EnsureValid(command.SeatIds);

        var attempt = await AttemptAsync(command, cancellationToken).ConfigureAwait(false);

        return attempt.LostRace
            ? (await AttemptAsync(command, cancellationToken).ConfigureAwait(false)).Result
            : attempt.Result;
    }

    private async Task<Attempt> AttemptAsync(
        SellSeatsCommand command,
        CancellationToken cancellationToken)
    {
        // One reading per attempt, as the other handlers take theirs, so every
        // seat in the batch is judged against the same instant.
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        var loaded = await _seats.GetByIdsAsync(command.SeatIds, cancellationToken).ConfigureAwait(false);

        // Checked, never trusted — as with holding. A seat reached through
        // another event's route is reported missing rather than sold.
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

            // A rejected attempt may have left events on this instance describing
            // a sale that never happened. They must not survive into the one that
            // does.
            seat.ClearDomainEvents();

            if (TrySell(seat, command.ClientId, utcNow) is { } refusal)
            {
                refusals.Add(new SeatSaleRefusal(seatId, refusal));
            }
        }

        // Every state change a seat makes raises an event, so these are the seats
        // this attempt actually moved. None means every seat was already this
        // client's, and there is nothing to write.
        var sold = seats.Values.Where(seat => seat.DomainEvents.Count > 0).ToList();

        if (refusals.Count > 0)
        {
            // The seats that did sell still read Sold in memory. Reading them
            // again throws that away, so a later save on this unit of work cannot
            // quietly sell what this attempt refused to.
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

    /// <summary>
    /// Asks one seat to sell, and returns why it would not, or nothing if it did.
    /// </summary>
    private static SellSeatOutcome? TrySell(Seat seat, Guid clientId, DateTime utcNow)
    {
        try
        {
            seat.Sell(clientId, utcNow);

            return null;
        }
        catch (SeatTransitionException ex) when (ex.Reason is SeatTransitionReason.SeatAlreadySold)
        {
            // The seat is sold — but to whom? If it is this client, their purchase
            // already went through and this is a retry, not a failure.
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

        // Any further SeatTransitionReason is left to propagate: Sell() refuses for
        // exactly the four reasons handled above, and a fifth would mean the
        // aggregate's contract moved without this handler being told.
    }

    private sealed record Attempt(SellSeatsResult Result, bool LostRace);
}
