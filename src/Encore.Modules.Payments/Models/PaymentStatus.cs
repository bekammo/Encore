namespace Encore.Modules.Payments.Models;

/// <summary>
/// Where an attempt to charge for an order has got to.
/// </summary>
/// <remarks>
/// <para>
/// The division that matters is not terminal-versus-not, it is
/// <see cref="Payment.IsLive"/>: whether this attempt could still be holding or
/// have taken the customer's money. Four of these seven are live, and the partial
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
    Voided = 5,

    /// <summary>
    /// Reconciliation established that the gateway never received this attempt, so
    /// nothing was ever held and nothing ever will be. Terminal, and not live.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is not <see cref="Declined"/>.</b> Both are terminal endings that
    /// moved no money, but a decline is an answer the gateway gave and this is the
    /// absence of one. Collapsing them would tell a customer their card was refused
    /// when it was never asked — which is the mistake <c>DECISIONS.md</c> 031
    /// rejected when it refused to treat a timeout as a decline, arriving one step
    /// later.
    /// </para>
    /// <para>
    /// <b>Why this is not <see cref="Voided"/> either.</b> A void releases an
    /// authorisation that existed. There was none here, and a row saying otherwise
    /// would send anybody chasing it to the gateway for a reference that does not
    /// exist.
    /// </para>
    /// <para>
    /// Reached only from <see cref="TimedOut"/>, and only by
    /// <see cref="Payment.ResolveAsAbandoned"/>. See <c>DECISIONS.md</c> 057.
    /// </para>
    /// </remarks>
    Abandoned = 6
}
