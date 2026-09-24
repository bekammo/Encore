namespace Encore.Modules.Payments.Contracts;

public static class PaymentsServiceApi
{
    public const string ServiceTokenHeader = "X-Service-Token";

    /// <summary>
    /// Every success <c>outcome</c> and refusal <c>reason</c>. The client reads an unknown one as
    /// a timeout, so a spelling the two sides disagree on would fail silently (018).
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
