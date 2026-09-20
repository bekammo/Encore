using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.Application;

/// <summary>
/// The "complete my checkout" use case: turn this client's live hold into a
/// confirmed sale. Sequences the ports exactly as
/// <see cref="HoldSeatCommandHandler"/> does — lock, load, let the domain decide,
/// persist — because the write it guards is the same single row.
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
/// <b>Selling a seat this client already bought is a success, not a refusal.</b>
/// <see cref="Domain.Seat"/> keeps <c>HeldByClientId</c> when it sells, precisely
/// so the row can still answer "who owns this", and that is what makes the
/// distinction possible here. A customer whose response was lost, or who
/// double-submitted the checkout form, is asking for a state that already holds —
/// telling them their own completed purchase failed would be both untrue and
/// alarming. This mirrors a hold being idempotent for the client that already has
/// it, and raises no second <see cref="Domain.Events.SeatSold"/>, because nothing
/// happened.
/// </para>
/// </remarks>
public sealed class SellSeatCommandHandler(
    ISeatRepository seats,
    IDistributedLock distributedLock,
    TimeProvider timeProvider)
{
    private readonly ISeatRepository _seats = seats;
    private readonly IDistributedLock _distributedLock = distributedLock;
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
        var resource = SeatLocks.ForSeat(command.SeatId);

        // One lock, and it is purely an optimisation. Contended or unreachable,
        // the attempt proceeds either way: this is a single-row write, so the
        // concurrency token settles the race and Sold is terminal in the
        // aggregate. Unlike holding, there is no cap here for a missed lock to
        // undermine, so there is nothing to refuse for.
        var seatLock = await _distributedLock
            .TryAcquireAsync(resource, SeatLocks.Ttl, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var result = await AttemptAsync(command, cancellationToken).ConfigureAwait(false);

            return result.Outcome is SellSeatOutcome.LostRace
                ? await AttemptAsync(command, cancellationToken).ConfigureAwait(false)
                : result;
        }
        finally
        {
            await _distributedLock.ReleaseIfHeldAsync(resource, seatLock).ConfigureAwait(false);
        }
    }

    private async Task<SellSeatResult> AttemptAsync(
        SellSeatCommand command,
        CancellationToken cancellationToken)
    {
        // One reading per attempt, as the other two handlers take theirs. Bound
        // here rather than read at the call below so that a second use cannot
        // quietly become a second clock reading.
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        var seat = await _seats.GetByIdAsync(command.SeatId, cancellationToken).ConfigureAwait(false);

        // Checked, never trusted — as with holding. A seat reached through
        // another event's route is reported missing rather than sold.
        if (seat is null || seat.EventId != command.EventId)
        {
            return SellSeatResult.SeatNotFound;
        }

        // A rejected attempt may have left events on this instance describing a
        // sale that never happened. They must not survive into the one that does.
        seat.ClearDomainEvents();

        try
        {
            seat.Sell(command.ClientId, utcNow);
            await _seats.SaveAsync(seat, cancellationToken).ConfigureAwait(false);

            return SellSeatResult.Sold;
        }
        catch (SeatTransitionException ex) when (ex.Reason is SeatTransitionReason.SeatAlreadySold)
        {
            // The seat is sold — but to whom? If it is this client, their purchase
            // already went through and this is a retry, not a failure.
            return seat.HeldByClientId == command.ClientId
                ? SellSeatResult.Sold
                : SellSeatResult.AlreadySold;
        }
        catch (SeatTransitionException ex) when (ex.Reason is SeatTransitionReason.NotTheHolder)
        {
            return SellSeatResult.NotTheHolder;
        }
        catch (SeatTransitionException ex) when (ex.Reason is SeatTransitionReason.HoldExpired)
        {
            return SellSeatResult.HoldExpired;
        }
        catch (SeatTransitionException ex) when (ex.Reason is SeatTransitionReason.NoActiveHold)
        {
            return SellSeatResult.NoActiveHold;
        }
        catch (ConcurrentSeatModificationException)
        {
            return SellSeatResult.LostRace;
        }

        // Any further SeatTransitionReason is left to propagate: Sell() refuses for
        // exactly the four reasons handled above, and a fifth would mean the
        // aggregate's contract moved without this handler being told.
    }

}
