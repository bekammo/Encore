namespace Encore.Modules.Payments.Models;

/// <summary>Why a <see cref="Payment"/> refused a transition. A closed set.</summary>
public enum PaymentTransitionReason
{
    /// <summary>
    /// The attempt is no longer waiting for the gateway's first answer.
    /// </summary>
    NotPending = 0,

    /// <summary>
    /// A capture or void with no live authorisation behind it.
    /// </summary>
    NotAuthorized = 1,

    /// <summary>
    /// A void on money already taken, which would be a refund.
    /// </summary>
    AlreadyCaptured = 2,

    /// <summary>
    /// A retry or reconciliation on an attempt that did get an answer.
    /// </summary>
    NotTimedOut = 3
}
