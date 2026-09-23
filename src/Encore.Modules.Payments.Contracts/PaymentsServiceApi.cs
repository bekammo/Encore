namespace Encore.Modules.Payments.Contracts;

/// <summary>
/// The strings both sides of the Payments service API must spell identically. Addresses and
/// tokens are configuration and do not belong here. The request bodies are the contract's own
/// request records, so they need no spelling of their own.
/// </summary>
public static class PaymentsServiceApi
{
    /// <summary>The header carrying the shared service token.</summary>
    public const string ServiceTokenHeader = "X-Service-Token";

    /// <summary>
    /// Every <c>outcome</c> a success carries and every <c>reason</c> a refusal carries. The
    /// client reads an unknown one as a timeout, so a spelling the two sides disagree on would
    /// fail silently. That is why both sides use these.
    /// </summary>
    public static class Outcomes
    {
        public const string Authorized = "authorized";

        public const string AlreadyCaptured = "already_captured";

        public const string Declined = "declined";

        public const string TimedOut = "timed_out";

        public const string ConcurrentAttemptInFlight = "concurrent_attempt_in_flight";

        public const string Captured = "captured";

        public const string Voided = "voided";

        public const string NoAuthorization = "no_authorization";
    }
}
