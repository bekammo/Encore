namespace Encore.Modules.Payments.Models;

/// <summary>
/// Unlike a seat refusal this signals a bug, not a race; a gateway decline is a successful
/// transition to <see cref="PaymentStatus.Declined"/>.
/// </summary>
public sealed class PaymentTransitionException : Exception
{
    public PaymentTransitionException(Guid paymentId, PaymentTransitionReason reason)
        : base($"Payment {paymentId} refused the transition: {reason}.")
    {
        PaymentId = paymentId;
        Reason = reason;
    }

    public Guid PaymentId { get; }

    public PaymentTransitionReason Reason { get; }
}
