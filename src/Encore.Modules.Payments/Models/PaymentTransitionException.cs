namespace Encore.Modules.Payments.Models;

/// <summary>
/// A payment refused a transition. Unlike a seat refusal this signals a bug, not a race;
/// a decline from the gateway is a successful transition to <see cref="PaymentStatus.Declined"/>.
/// </summary>
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
