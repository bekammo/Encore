namespace Encore.Modules.Payments.Models;

/// <summary>
/// Why a <see cref="Payment"/> refused a transition. A closed set, carried by
/// <see cref="PaymentTransitionException"/>.
/// </summary>
/// <remarks>
/// The same shape as <c>SeatTransitionReason</c> and for the same reason: the
/// failure set is small, and the natural consumer switches on it rather than
/// catching a type per case.
/// </remarks>
public enum PaymentTransitionReason
{
    /// <summary>
    /// An authorise, decline or time-out was attempted on an attempt that is no
    /// longer waiting for the gateway's first answer.
    /// </summary>
    NotPending = 0,

    /// <summary>
    /// A capture or a void was attempted with no live authorisation behind it.
    /// There is nothing to take and nothing to release.
    /// </summary>
    NotAuthorized = 1,

    /// <summary>
    /// A void was attempted on money already taken. Kept apart from
    /// <see cref="NotAuthorized"/> because it is the one case where the caller is
    /// asking for something real that this system cannot do — a refund — rather
    /// than something incoherent.
    /// </summary>
    AlreadyCaptured = 2,

    /// <summary>
    /// A retry, or one of the three reconciliation transitions, was attempted on an
    /// attempt that did get an answer. Only the ambiguity of
    /// <see cref="PaymentStatus.TimedOut"/> justifies reusing a row and its
    /// idempotency key, or settling an attempt on an answer looked up rather than
    /// received; everything else already knows how it ended.
    /// </summary>
    NotTimedOut = 3
}
