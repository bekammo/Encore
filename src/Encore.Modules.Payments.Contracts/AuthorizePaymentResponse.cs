namespace Encore.Modules.Payments.Contracts;

/// <summary>
/// What came back from an authorisation attempt.
/// </summary>
/// <param name="Status">How it turned out.</param>
/// <param name="PaymentId">
/// The attempt that was recorded, when one was. Null only when nothing could be
/// recorded at all.
/// </param>
public sealed record AuthorizePaymentResponse(
    AuthorizePaymentStatus Status,
    Guid? PaymentId = null)
{
    /// <summary>The funds are held.</summary>
    public static AuthorizePaymentResponse Authorized(Guid paymentId) =>
        new(AuthorizePaymentStatus.Authorized, paymentId);

    /// <summary>The gateway refused. Nothing was held.</summary>
    public static AuthorizePaymentResponse Declined(Guid paymentId) =>
        new(AuthorizePaymentStatus.Declined, paymentId);

    /// <summary>No answer came back, so the outcome at the gateway is unknown.</summary>
    public static AuthorizePaymentResponse TimedOut(Guid paymentId) =>
        new(AuthorizePaymentStatus.TimedOut, paymentId);

    /// <summary>This order has already been paid for.</summary>
    public static AuthorizePaymentResponse AlreadyCaptured(Guid paymentId) =>
        new(AuthorizePaymentStatus.AlreadyCaptured, paymentId);

    /// <summary>Another attempt got there first and has not finished.</summary>
    public static AuthorizePaymentResponse ConcurrentAttemptInFlight { get; } =
        new(AuthorizePaymentStatus.ConcurrentAttemptInFlight);
}
