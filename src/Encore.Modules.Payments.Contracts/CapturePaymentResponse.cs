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
    /// <summary><see cref="CapturePaymentStatus.Captured"/>.</summary>
    public static CapturePaymentResponse Captured(Guid paymentId) =>
        new(CapturePaymentStatus.Captured, paymentId);

    /// <summary><see cref="CapturePaymentStatus.NoAuthorization"/>.</summary>
    public static CapturePaymentResponse NoAuthorization { get; } =
        new(CapturePaymentStatus.NoAuthorization);

    /// <summary><see cref="CapturePaymentStatus.TimedOut"/>.</summary>
    public static CapturePaymentResponse TimedOut(Guid paymentId) =>
        new(CapturePaymentStatus.TimedOut, paymentId);
}
