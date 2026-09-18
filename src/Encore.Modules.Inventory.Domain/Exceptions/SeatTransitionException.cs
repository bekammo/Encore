namespace Encore.Modules.Inventory.Domain.Exceptions;

/// <summary>
/// Thrown when a seat refuses a transition because the rules do not permit it
/// from its current state.
/// </summary>
/// <remarks>
/// One exception type carrying a <see cref="SeatTransitionReason"/> rather than a
/// type per case: the failure set is small and closed, and the natural consumer is
/// a handler switching on the reason to choose a response. Like
/// <see cref="ConcurrentSeatModificationException"/>, a refusal here is an expected
/// business outcome under contention, not a fault.
/// </remarks>
public sealed class SeatTransitionException : Exception
{
    /// <summary>Creates the exception for a seat and a reason.</summary>
    public SeatTransitionException(Guid seatId, SeatTransitionReason reason)
        : base($"Seat {seatId} refused the transition: {reason}.")
    {
        SeatId = seatId;
        Reason = reason;
    }

    /// <summary>The seat that refused.</summary>
    public Guid SeatId { get; }

    /// <summary>Why it refused.</summary>
    public SeatTransitionReason Reason { get; }
}
