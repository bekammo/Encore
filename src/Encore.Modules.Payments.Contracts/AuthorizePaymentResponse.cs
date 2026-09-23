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
    /// <summary><see cref="AuthorizePaymentStatus.Authorized"/>.</summary>
    public static AuthorizePaymentResponse Authorized(Guid paymentId) =>
        new(AuthorizePaymentStatus.Authorized, paymentId);

    /// <summary><see cref="AuthorizePaymentStatus.Declined"/>.</summary>
    public static AuthorizePaymentResponse Declined(Guid paymentId) =>
        new(AuthorizePaymentStatus.Declined, paymentId);

    /// <summary><see cref="AuthorizePaymentStatus.TimedOut"/>.</summary>
    public static AuthorizePaymentResponse TimedOut(Guid paymentId) =>
        new(AuthorizePaymentStatus.TimedOut, paymentId);

    /// <summary><see cref="AuthorizePaymentStatus.AlreadyCaptured"/>.</summary>
    public static AuthorizePaymentResponse AlreadyCaptured(Guid paymentId) =>
        new(AuthorizePaymentStatus.AlreadyCaptured, paymentId);

    /// <summary><see cref="AuthorizePaymentStatus.ConcurrentAttemptInFlight"/>.</summary>
    public static AuthorizePaymentResponse ConcurrentAttemptInFlight { get; } =
        new(AuthorizePaymentStatus.ConcurrentAttemptInFlight);
}
