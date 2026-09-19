namespace Encore.Modules.Payments.Contracts;

/// <summary>
/// How a void attempt turned out. A closed set.
/// </summary>
public enum VoidPaymentStatus
{
    /// <summary>The hold is released and nothing was taken.</summary>
    Voided = 0,

    /// <summary>
    /// There is nothing held against this order to release — no attempt, one that
    /// was declined, or one already voided. Not an error for a caller unwinding a
    /// failed confirm: it means there is nothing to unwind, which is why a repeated
    /// void lands here rather than on a failure.
    /// </summary>
    NoAuthorization = 1,

    /// <summary>
    /// The money has already been taken. Releasing it would be a refund, which
    /// this system does not have, so the caller is told plainly rather than being
    /// given a success that moved nothing.
    /// </summary>
    AlreadyCaptured = 2,

    /// <summary>
    /// The gateway did not answer. The hold stays recorded and will lapse at the
    /// gateway on its own, which is the benign half of this design: an
    /// authorisation nobody captures costs the customer nothing.
    /// </summary>
    TimedOut = 3
}
