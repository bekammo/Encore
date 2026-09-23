namespace Encore.Modules.Inventory.Domain.Exceptions;

/// <summary>
/// A seat refused a transition. One type with a closed <see cref="SeatTransitionReason"/>,
/// so callers switch on the reason rather than on exception types.
/// </summary>
public sealed class SeatTransitionException : Exception
{
    public SeatTransitionException(Guid seatId, SeatTransitionReason reason)
        : base($"Seat {seatId} refused the transition: {reason}.")
    {
        SeatId = seatId;
        Reason = reason;
    }

    public Guid SeatId { get; }

    public SeatTransitionReason Reason { get; }
}
