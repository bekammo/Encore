namespace Encore.Modules.Payments.Data;

/// <summary>
/// Knobs for <see cref="PaymentReconciler"/>.
/// </summary>
/// <remarks>
/// Every default here is a starting point rather than a measured one, the same
/// admission <c>OutboxOptions</c> makes. Nothing in the load harness drives the
/// confirm path hard enough to have an opinion yet.
/// </remarks>
public sealed class PaymentReconciliationOptions
{
    /// <summary>Configuration section: <c>Payments:Reconciliation</c>.</summary>
    public const string SectionName = "Payments:Reconciliation";

    /// <summary>
    /// Whether the reconciler runs at all.
    /// </summary>
    /// <remarks>
    /// On by default, for <c>OutboxOptions.Enabled</c>'s reason rather than
    /// <c>MigrateOnStartup</c>'s: a reconciler that did not run by default would
    /// silently leave customers' funds held, so the safe default is on and an
    /// operator opts out. Turning it off is also how the claim that no invariant
    /// depends on it gets tested rather than asserted — every existing Payments
    /// test passes with this off, because all of them were written before it
    /// existed.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long to wait between sweeps.
    /// </summary>
    /// <remarks>
    /// <b>Minutes, not the outbox's one second, and the gap is the point.</b> An
    /// undelivered event is a fact the system already owns and is merely late in
    /// passing on; an unresolved authorisation is a question only a third party can
    /// answer, and asking it more often does not make the answer arrive sooner. A
    /// timed-out authorisation is a minutes-to-hours problem, so this polls on that
    /// scale — which is also what keeps a third background workload from repeating
    /// what <c>DECISIONS.md</c> 056 measured the dispatcher doing to the request
    /// path.
    /// </remarks>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How many attempts one sweep looks at. Each one costs a gateway round trip,
    /// and one that turns out to be holding funds costs two.
    /// </summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>
    /// How long an attempt must have been timed out before this touches it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One seat-hold duration, and that is where the number comes from.</b> A
    /// confirm whose authorisation timed out aborts before selling anything, so the
    /// customer's only route back is another confirm — which finds the row, retries
    /// it, and asks the gateway the same question under the same key. Past five
    /// minutes the seats that confirm was for have certainly expired, so no confirm
    /// that could still succeed is racing this sweep.
    /// </para>
    /// <para>
    /// The race is survivable either way — the row carries <c>xmin</c> and both
    /// sides lose gracefully — so this is about not doing pointless work and not
    /// voiding an authorisation a customer is seconds away from using, rather than
    /// about correctness.
    /// </para>
    /// </remarks>
    public TimeSpan MinimumAge { get; set; } = TimeSpan.FromMinutes(5);
}
