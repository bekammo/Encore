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
    /// The gateway said no. Nothing was held and nothing was taken, so the
    /// customer can try again — with a different card, which is a genuinely new
    /// attempt.
    /// </summary>
    Declined = 1,

    /// <summary>
    /// The gateway did not answer, so whether funds are held is unknown. The
    /// attempt is recorded and keeps its idempotency key, so a retry asks the same
    /// question rather than a second one. See <c>DECISIONS.md</c> 031.
    /// </summary>
    TimedOut = 2,

    /// <summary>
    /// This order has already been paid for. Reached by a confirm retried after a
    /// complete success, which must not charge anybody twice.
    /// </summary>
    AlreadyCaptured = 3,

    /// <summary>
    /// Another attempt against this order was recorded first and is still in
    /// flight. Retriable, and the name is borrowed deliberately from
    /// <c>HoldSeatStatus.ConcurrentRequestInFlight</c> (<c>DECISIONS.md</c> 010) —
    /// it is the same situation and deserves the same word.
    /// </summary>
    ConcurrentAttemptInFlight = 4
}
