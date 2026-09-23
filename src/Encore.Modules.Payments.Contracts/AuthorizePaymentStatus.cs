namespace Encore.Modules.Payments.Contracts;

/// <summary>
/// How an authorisation attempt turned out. A closed set.
/// </summary>
public enum AuthorizePaymentStatus
{
    /// <summary>
    /// The funds are held. Also the answer when this order already had a live
    /// authorisation, because a retry must not produce a second one.
    /// </summary>
    Authorized = 0,

    /// <summary>
    /// The gateway said no. Nothing was held; the customer can try another card.
    /// </summary>
    Declined = 1,

    /// <summary>
    /// The gateway did not answer. The attempt keeps its idempotency key, so a retry asks
    /// the same question.
    /// </summary>
    TimedOut = 2,

    /// <summary>
    /// This order has already been paid for.
    /// </summary>
    AlreadyCaptured = 3,

    /// <summary>
    /// Another attempt against this order is still in flight. Retryable.
    /// </summary>
    ConcurrentAttemptInFlight = 4
}
