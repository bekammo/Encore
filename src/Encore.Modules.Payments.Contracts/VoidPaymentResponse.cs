namespace Encore.Modules.Payments.Contracts;

/// <summary>
/// What came back from a void attempt.
/// </summary>
/// <param name="Status">How it turned out.</param>
/// <param name="PaymentId">The attempt that was released, if there was one.</param>
public sealed record VoidPaymentResponse(
    VoidPaymentStatus Status,
    Guid? PaymentId = null)
{
    /// <summary>The hold is released and nothing was taken.</summary>
    public static VoidPaymentResponse Voided(Guid paymentId) =>
        new(VoidPaymentStatus.Voided, paymentId);

    /// <summary>Nothing is held against this order.</summary>
    public static VoidPaymentResponse NoAuthorization { get; } =
        new(VoidPaymentStatus.NoAuthorization);

    /// <summary>The money has already been taken; releasing it would be a refund.</summary>
    public static VoidPaymentResponse AlreadyCaptured(Guid paymentId) =>
        new(VoidPaymentStatus.AlreadyCaptured, paymentId);

    /// <summary>No answer came back. The hold will lapse at the gateway on its own.</summary>
    public static VoidPaymentResponse TimedOut(Guid paymentId) =>
        new(VoidPaymentStatus.TimedOut, paymentId);
}
