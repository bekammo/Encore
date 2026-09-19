namespace Encore.Modules.Payments.Models;

/// <summary>
/// Thrown when a payment refuses a transition because the rules do not permit it
/// from its current state.
/// </summary>
/// <remarks>
/// One exception type carrying a <see cref="PaymentTransitionReason"/> rather than
/// a type per case, mirroring <c>SeatTransitionException</c>. Unlike a seat's
/// refusal this one is not an expected outcome under contention — a caller that
/// reaches it has asked for something the state machine never permits, which is a
/// bug rather than a race. The gateway saying no is not this: that is
/// <see cref="PaymentStatus.Declined"/>, reached by a transition that succeeded.
/// </remarks>
public sealed class PaymentTransitionException : Exception
{
    /// <summary>Creates the exception for a payment and a reason.</summary>
    public PaymentTransitionException(Guid paymentId, PaymentTransitionReason reason)
        : base($"Payment {paymentId} refused the transition: {reason}.")
    {
        PaymentId = paymentId;
        Reason = reason;
    }

    /// <summary>The payment that refused.</summary>
    public Guid PaymentId { get; }

    /// <summary>Why it refused.</summary>
    public PaymentTransitionReason Reason { get; }
}
