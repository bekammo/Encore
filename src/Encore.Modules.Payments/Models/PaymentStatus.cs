namespace Encore.Modules.Payments.Models;

/// <summary>
/// Where a payment attempt has got to. What matters most is <see cref="Payment.IsLive"/>:
/// whether the attempt might hold or have taken money.
/// </summary>
/// <remarks>
/// Numbered explicitly: the partial unique index lists these values as a SQL literal, so
/// renumbering would silently change what it guards.
/// </remarks>
public enum PaymentStatus
{
    /// <summary>Recorded, awaiting the gateway. Live: the request may already have arrived.</summary>
    Pending = 0,

    /// <summary>Funds held; a capture or void is still to come. Live.</summary>
    Authorized = 1,

    /// <summary>Money taken. Terminal and live.</summary>
    Captured = 2,

    /// <summary>The gateway refused. Terminal, not live: the customer may try again.</summary>
    Declined = 3,

    /// <summary>
    /// The authorisation got no answer. Live, because unknown may mean yes; a retry reuses
    /// this row and its idempotency key.
    /// </summary>
    TimedOut = 4,

    /// <summary>Released without capture. Terminal, not live.</summary>
    Voided = 5,

    /// <summary>
    /// Reconciliation found the gateway never received the attempt. Terminal, not live.
    /// Not <see cref="Declined"/> (nobody refused) and not <see cref="Voided"/> (nothing was held).
    /// </summary>
    Abandoned = 6
}
