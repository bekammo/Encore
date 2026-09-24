namespace Encore.Modules.Payments.Contracts;

public enum AuthorizePaymentStatus
{
    /// <summary>Also the answer when the order already had a live authorisation.</summary>
    Authorized = 0,

    Declined = 1,

    /// <summary>The attempt keeps its idempotency key, so a retry asks the same question.</summary>
    TimedOut = 2,

    /// <summary>An answer, not a refusal.</summary>
    AlreadyCaptured = 3,

    ConcurrentAttemptInFlight = 4
}
