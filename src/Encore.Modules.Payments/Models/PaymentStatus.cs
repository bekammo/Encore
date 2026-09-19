namespace Encore.Modules.Payments.Models;

/// <summary>
/// Where an attempt to charge for an order has got to.
/// </summary>
/// <remarks>
/// <para>
/// The division that matters is not terminal-versus-not, it is
/// <see cref="Payment.IsLive"/>: whether this attempt could still be holding or
/// have taken the customer's money. Four of these six are live, and the partial
/// unique index in <c>PaymentConfiguration</c> filters on exactly that set to
/// guarantee an order is never charged twice. The reasoning is
/// <c>DECISIONS.md</c> 030.
/// </para>
/// <para>
/// <b>Numbered explicitly, and the numbers are load-bearing.</b> The index filter
/// is a SQL literal listing these values, and no compiler checks it against this
/// enum. Renumbering a member would leave the index quietly guarding the wrong
/// rows — the same trap Orders documents on <c>OrderStatus.Pending</c>.
/// </para>
/// </remarks>
public enum PaymentStatus
{
    /// <summary>
    /// The attempt has been recorded and the gateway has not yet answered. Live,
    /// because a request in flight may already have reached the other end.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// The gateway is holding the funds. Not an ending — a capture or a void is
    /// still to come — and live, obviously.
    /// </summary>
    Authorized = 1,

    /// <summary>
    /// The money has been taken. Terminal and live: undoing it is a refund, which
    /// this system does not have.
    /// </summary>
    Captured = 2,

    /// <summary>
    /// The gateway said no. Terminal, and the one ending that definitively moved
    /// nothing, so it does not hold the order's live-attempt slot — the customer
    /// may try again with a different card, and that is a new attempt.
    /// </summary>
    Declined = 3,

    /// <summary>
    /// The authorisation got no answer, so whether funds are held is unknown.
    /// Live, because the honest reading of "unknown" is "possibly yes", and
    /// <see cref="Payment.Retry"/> reuses this row so the retry can carry the same
    /// idempotency key. See <c>DECISIONS.md</c> 031.
    /// </summary>
    /// <remarks>
    /// Reached only from <see cref="Pending"/>. A <i>capture</i> that times out
    /// leaves the payment <see cref="Authorized"/>, because that is still exactly
    /// what is true.
    /// </remarks>
    TimedOut = 4,

    /// <summary>
    /// The authorisation was released without being captured. Terminal, and not
    /// live: no money moved and none will.
    /// </summary>
    Voided = 5
}
