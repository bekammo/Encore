namespace Encore.Modules.Payments.Contracts;

/// <summary>
/// How a capture attempt turned out. A closed set, and a deliberately short one.
/// </summary>
/// <remarks>
/// <b>There is no <c>Declined</c> here.</b> A real gateway can refuse a capture —
/// an authorisation that lapsed or was revoked — but modelling that honestly needs
/// a real gateway's error taxonomy, and inventing one for a simulator would be
/// guessing at a vocabulary. An authorisation granted is treated here as a
/// commitment that will be honoured or not answered. This is a known simplification
/// rather than a claim about payments, and it is recorded as one in
/// <c>DECISIONS.md</c> 032.
/// </remarks>
public enum CapturePaymentStatus
{
    /// <summary>The money has been taken. Also the answer to a repeated capture.</summary>
    Captured = 0,

    /// <summary>
    /// There is nothing held against this order to capture — no attempt, or one
    /// that was declined, voided or never authorised.
    /// </summary>
    NoAuthorization = 1,

    /// <summary>
    /// The gateway did not answer. The funds stay held and the attempt stays
    /// capturable, so this is safe to retry and the order waits rather than fails.
    /// </summary>
    TimedOut = 2
}
