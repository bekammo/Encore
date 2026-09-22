namespace Encore.Modules.Payments.Simulation;

/// <summary>
/// One decision the gateway has made, and will keep making, for an idempotency
/// key. The simulator's own record — not Payments' data.
/// </summary>
/// <remarks>
/// <para>
/// <b>This row stands in for the third party's durable store, and that is the whole
/// point of it.</b> Until <c>DECISIONS.md</c> 066 the same facts lived in a
/// dictionary on a singleton, which made "every process running
/// <c>PaymentReconciler</c> asks the same gateway" a precondition the code depended
/// on and nothing stated. An ordinary restart was enough to break it: 064's first
/// fault restarted <c>payments-api</c> and the sweep then settled 120 of 121
/// attempts as <c>Abandoned</c> — "the gateway looked and there is nothing there" —
/// while the funds behind them were still held.
/// </para>
/// <para>
/// <b>It lives in the <c>payments</c> schema, which is a compromise worth naming.</b>
/// A real gateway's records are not in our database at all, and a separate schema
/// with a context and a migrator of its own would say so more clearly. It would also
/// be a third <c>DbContext</c>, a sixth connection string and another startup
/// migrator in exchange for a distinction no test can observe. So it shares the
/// schema and says here what it is: nothing in Payments' own logic reads this table,
/// and the only type that touches it is <see cref="SimulatedPaymentGateway"/>.
/// </para>
/// <para>
/// <b>Only decided outcomes are recorded.</b> A request that never arrived leaves no
/// row, which is what makes a later lookup's <c>NotFound</c> mean something (057). A
/// row exists exactly when the gateway decided, whether or not the caller ever heard
/// the answer.
/// </para>
/// </remarks>
internal sealed class GatewayLedgerEntry
{
    /// <summary>The key the caller presented. The gateway's identity for the attempt.</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>
    /// What the gateway decided. Only <see cref="GatewayOutcome.Succeeded"/> and
    /// <see cref="GatewayOutcome.Declined"/> ever reach this column —
    /// <see cref="GatewayOutcome.TimedOut"/> is a thing that happened to the
    /// caller, not a decision the gateway made.
    /// </summary>
    public required GatewayOutcome Outcome { get; init; }

    /// <summary>When the decision was made. Diagnostics only; nothing branches on it.</summary>
    public required DateTime RecordedAt { get; init; }
}
