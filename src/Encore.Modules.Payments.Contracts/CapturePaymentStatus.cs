namespace Encore.Modules.Payments.Contracts;

public enum CapturePaymentStatus
{
    /// <summary>Also the answer to a repeated capture.</summary>
    Captured = 0,

    /// <summary>Also while the attempt is pending or timed out.</summary>
    NoAuthorization = 1,

    /// <summary>The funds stay held, so a retry is safe.</summary>
    TimedOut = 2,

    /// <summary>
    /// The gateway refused the money it had authorised, and nothing is held any more. Retrying
    /// this capture cannot help; a fresh authorisation can (034).
    /// </summary>
    Declined = 3
}
