using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Encore.Modules.Inventory.Adapters.Telemetry;

/// <summary>
/// BCL instruments only, recorded at the adapters' edges, never in the Domain or the use cases
/// (021). Static rather than from <c>IMeterFactory</c>: one set per process either way, and the
/// adapters that record are also built by hand in tests.
/// </summary>
internal static class InventoryTelemetry
{
    /// <summary>Keeps the <c>Encore.</c> prefix: hosts subscribe to <c>Encore.*</c> (021).</summary>
    public const string Name = "Encore.Inventory";

    public static readonly ActivitySource Source = new(Name);

    private static readonly Meter Meter = new(Name);

    public static readonly Counter<long> SeatOutcomes = Meter.CreateCounter<long>(
        "encore.inventory.seat.outcomes",
        unit: "{seat}",
        description: "Seat actions answered, by action and outcome.");

    /// <summary>Redis lock calls, including attempts the cooldown refused without asking Redis.</summary>
    public static readonly Counter<long> LockAttempts = Meter.CreateCounter<long>(
        "encore.inventory.lock.attempts",
        unit: "{attempt}",
        description: "Distributed lock calls, by operation and outcome.");

    public static readonly Counter<long> OutboxDeliveries = Meter.CreateCounter<long>(
        "encore.inventory.outbox.deliveries",
        unit: "{message}",
        description: "Outbox delivery attempts, by event type and outcome.");

    public static readonly Histogram<double> OutboxDeliveryLag = Meter.CreateHistogram<double>(
        "encore.inventory.outbox.delivery.lag",
        unit: "s",
        description: "Time from a seat change to the delivery of its event.",
        tags: null,
        // The SDK's default boundaries (0, 5, 10, 25…) suit milliseconds and would put every
        // healthy delivery in the first bucket.
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60]
        });

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
