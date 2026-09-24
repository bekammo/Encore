namespace Encore.Modules.Payments.Simulation;

/// <summary>Stands in for a real gateway's durable store: nothing in Payments' own logic reads it.</summary>
internal sealed class GatewayLedgerEntry
{
    public required string IdempotencyKey { get; init; }

    /// <summary>Succeeded or Declined; a timeout is not a gateway decision.</summary>
    public required GatewayOutcome Outcome { get; init; }

    public required DateTime RecordedAt { get; init; }
}
