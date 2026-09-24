namespace Encore.Modules.Payments.Contracts;

public sealed record AuthorizePaymentResponse(
    AuthorizePaymentStatus Status,
    Guid? PaymentId = null)
{
    public static AuthorizePaymentResponse Authorized(Guid paymentId) =>
        new(AuthorizePaymentStatus.Authorized, paymentId);

    public static AuthorizePaymentResponse Declined(Guid paymentId) =>
        new(AuthorizePaymentStatus.Declined, paymentId);

    public static AuthorizePaymentResponse TimedOut(Guid paymentId) =>
        new(AuthorizePaymentStatus.TimedOut, paymentId);

    public static AuthorizePaymentResponse AlreadyCaptured(Guid paymentId) =>
        new(AuthorizePaymentStatus.AlreadyCaptured, paymentId);

    public static AuthorizePaymentResponse ConcurrentAttemptInFlight { get; } =
        new(AuthorizePaymentStatus.ConcurrentAttemptInFlight);
}
