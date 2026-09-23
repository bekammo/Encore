using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Encore.Modules.Inventory.Adapters.Telemetry;

/// <summary>
/// Inventory's own traces and metrics, through the BCL alone: nothing here knows whether
/// anyone is listening, and nothing records unless something is. Recorded at the adapters'
/// edges, so the Domain and the use cases stay as they were.
/// </summary>
/// <remarks>
/// Static rather than from <c>IMeterFactory</c>: one set of instruments per process either way,
/// and the adapters that record are also built by hand in tests.
/// </remarks>
internal static class InventoryTelemetry
{
    /// <summary>The source and meter name. Hosts subscribe to <c>Encore.*</c>.</summary>
    public const string Name = "Encore.Inventory";

    public static readonly ActivitySource Source = new(Name);

    private static readonly Meter Meter = new(Name);

    /// <summary>What a seat answered, per seat, by action and outcome.</summary>
    public static readonly Counter<long> SeatOutcomes = Meter.CreateCounter<long>(
        "encore.inventory.seat.outcomes",
        unit: "{seat}",
        description: "Seat actions answered, by action and outcome.");

    /// <summary>What the lock service answered, including the attempts the cooldown answered for it.</summary>
    public static readonly Counter<long> LockAttempts = Meter.CreateCounter<long>(
        "encore.inventory.lock.attempts",
        unit: "{attempt}",
        description: "Distributed lock calls, by operation and outcome.");

    public static readonly Counter<long> OutboxDeliveries = Meter.CreateCounter<long>(
        "encore.inventory.outbox.deliveries",
        unit: "{message}",
        description: "Outbox delivery attempts, by event type and outcome.");

    /// <summary>From the seat change to its delivery: how late "late is not wrong" is.</summary>
    public static readonly Histogram<double> OutboxDeliveryLag = Meter.CreateHistogram<double>(
        "encore.inventory.outbox.delivery.lag",
        unit: "s",
        description: "Time from a seat change to the delivery of its event.");

    public static void RecordSeat(string action, Enum outcome) =>
        SeatOutcomes.Add(
            1,
            new KeyValuePair<string, object?>("action", action),
            new KeyValuePair<string, object?>("outcome", outcome.ToString()));

    public static void RecordLock(string operation, string outcome) =>
        LockAttempts.Add(
            1,
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("outcome", outcome));
}
