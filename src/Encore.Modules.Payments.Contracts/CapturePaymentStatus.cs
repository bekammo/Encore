namespace Encore.Modules.Payments.Contracts;

/// <summary>
/// How a capture attempt turned out. There is no <c>Declined</c>: the simulated gateway
/// honours every authorisation it granted, a known simplification.
/// </summary>
public enum CapturePaymentStatus
{
    /// <summary>The money has been taken. Also the answer to a repeated capture.</summary>
    Captured = 0,

    /// <summary>
    /// Nothing is held against this order to capture.
    /// </summary>
    NoAuthorization = 1,

    /// <summary>
    /// The gateway did not answer. The funds stay held, so this is safe to retry.
    /// </summary>
    TimedOut = 2
}
