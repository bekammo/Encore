namespace Encore.Modules.Payments.Simulation;

/// <summary>
/// One decision the simulated gateway made for an idempotency key. Stands in for a real
/// gateway's durable store: nothing in Payments' own logic reads it. Only decided outcomes
/// are recorded, so a lost request leaves no row.
/// </summary>
internal sealed class GatewayLedgerEntry
{
    public required string IdempotencyKey { get; init; }

    /// <summary>
    /// Succeeded or Declined; a timeout is not a gateway decision.
    /// </summary>
    public required GatewayOutcome Outcome { get; init; }

    /// <summary>When the decision was made. Diagnostics only; nothing branches on it.</summary>
    public required DateTime RecordedAt { get; init; }
}
