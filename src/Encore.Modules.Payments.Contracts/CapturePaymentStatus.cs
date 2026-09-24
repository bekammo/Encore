namespace Encore.Modules.Payments.Contracts;

/// <summary>
/// No <c>Declined</c>: the simulated gateway honours every authorisation it granted, a known
/// simplification (014).
/// </summary>
public enum CapturePaymentStatus
{
    /// <summary>Also the answer to a repeated capture.</summary>
    Captured = 0,

    /// <summary>Also while the attempt is pending or timed out.</summary>
    NoAuthorization = 1,

    /// <summary>The funds stay held, so a retry is safe.</summary>
    TimedOut = 2
}
