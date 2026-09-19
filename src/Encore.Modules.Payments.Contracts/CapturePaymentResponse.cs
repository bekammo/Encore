namespace Encore.Modules.Payments.Contracts;

/// <summary>
/// What came back from a capture attempt.
/// </summary>
/// <param name="Status">How it turned out.</param>
/// <param name="PaymentId">The attempt that was captured, if there was one.</param>
public sealed record CapturePaymentResponse(
    CapturePaymentStatus Status,
    Guid? PaymentId = null)
{
    /// <summary>The money has been taken.</summary>
    public static CapturePaymentResponse Captured(Guid paymentId) =>
        new(CapturePaymentStatus.Captured, paymentId);

    /// <summary>Nothing is held against this order.</summary>
    public static CapturePaymentResponse NoAuthorization { get; } =
        new(CapturePaymentStatus.NoAuthorization);

    /// <summary>No answer came back. The funds stay held and this can be retried.</summary>
    public static CapturePaymentResponse TimedOut(Guid paymentId) =>
        new(CapturePaymentStatus.TimedOut, paymentId);
}
