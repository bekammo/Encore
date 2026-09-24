namespace Encore.Modules.Payments.Contracts;

public sealed record CapturePaymentResponse(
    CapturePaymentStatus Status,
    Guid? PaymentId = null)
{
    public static CapturePaymentResponse Captured(Guid paymentId) =>
        new(CapturePaymentStatus.Captured, paymentId);

    public static CapturePaymentResponse NoAuthorization { get; } =
        new(CapturePaymentStatus.NoAuthorization);

    public static CapturePaymentResponse TimedOut(Guid paymentId) =>
        new(CapturePaymentStatus.TimedOut, paymentId);
}
