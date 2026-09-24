namespace Encore.Modules.Payments.Models;

/// <summary>Never renumber: the live-attempt index filters on these values as SQL literals.</summary>
public enum PaymentStatus
{
    Pending = 0,

    Authorized = 1,

    Captured = 2,

    Declined = 3,

    TimedOut = 4,

    Voided = 5,

    /// <summary>
    /// The gateway never received the attempt: not <see cref="Declined"/> (nobody refused), not
    /// <see cref="Voided"/> (nothing was held).
    /// </summary>
    Abandoned = 6
}
