namespace Encore.Modules.Payments.Contracts;

public sealed record VoidPaymentResponse(
    VoidPaymentStatus Status,
    Guid? PaymentId = null)
{
    public static VoidPaymentResponse Voided(Guid paymentId) =>
        new(VoidPaymentStatus.Voided, paymentId);

    public static VoidPaymentResponse NoAuthorization { get; } =
        new(VoidPaymentStatus.NoAuthorization);

    public static VoidPaymentResponse AlreadyCaptured(Guid paymentId) =>
        new(VoidPaymentStatus.AlreadyCaptured, paymentId);

    public static VoidPaymentResponse TimedOut(Guid paymentId) =>
        new(VoidPaymentStatus.TimedOut, paymentId);
}
