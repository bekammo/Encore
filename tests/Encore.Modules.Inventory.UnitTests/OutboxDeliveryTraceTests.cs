using System.Diagnostics;
using Encore.Modules.Inventory.Adapters.Messaging;
using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Adapters.Telemetry;

namespace Encore.Modules.Inventory.UnitTests;

/// <summary>
/// A delivery is its own trace with a link to the one that raised the event: the raising
/// request ended long before, and a redelivery must not give it a second child.
/// </summary>
public sealed class OutboxDeliveryTraceTests : IDisposable
{
    private static readonly DateTime OccurredAt = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly ActivityListener _listener = new()
    {
        ShouldListenTo = source => source.Name == InventoryTelemetry.Name,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
    };

    public OutboxDeliveryTraceTests() => ActivitySource.AddActivityListener(_listener);

    public void Dispose() => _listener.Dispose();

    [Fact]
    public void StartDelivery_WithATraceParent_ShouldLinkToItWithoutJoiningIt()
    {
        var raisedBy = ActivityContext.Parse(
            "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            traceState: null);

        using var delivery = OutboxDispatcher.StartDelivery(Message(
            "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"));

        Assert.NotNull(delivery);
        Assert.Equal(ActivityKind.Consumer, delivery.Kind);
        Assert.NotEqual(raisedBy.TraceId, delivery.TraceId);

        var link = Assert.Single(delivery.Links);
        Assert.Equal(raisedBy.TraceId, link.Context.TraceId);
        Assert.Equal(raisedBy.SpanId, link.Context.SpanId);
    }

    [Fact]
    public void StartDelivery_WithoutATraceParent_ShouldLinkToNothing()
    {
        using var delivery = OutboxDispatcher.StartDelivery(Message(traceParent: null));

        Assert.NotNull(delivery);
        Assert.Empty(delivery.Links);
    }

    /// <summary>A malformed value is not worth failing a delivery over.</summary>
    [Fact]
    public void StartDelivery_WithAMalformedTraceParent_ShouldLinkToNothing()
    {
        using var delivery = OutboxDispatcher.StartDelivery(Message("not-a-traceparent"));

        Assert.NotNull(delivery);
        Assert.Empty(delivery.Links);
    }

    /// <summary>
    /// Healthy deliveries take milliseconds. With the SDK's default boundaries, sized for
    /// milliseconds, every one landed in the 0–5 bucket and the dashboard read p99 = 5 s.
    /// </summary>
    [Fact]
    public void DeliveryLag_ShouldBucketSubSecondDelays()
    {
        var boundaries = InventoryTelemetry.OutboxDeliveryLag.Advice?.HistogramBucketBoundaries;

        Assert.NotNull(boundaries);
        Assert.True(boundaries[0] <= 0.01, $"The first boundary is {boundaries[0]} s.");
        Assert.Contains(1.0, boundaries);
    }

    private static OutboxMessage Message(string? traceParent) =>
        OutboxMessage.For(
            Guid.NewGuid(),
            "inventory.seat.held.v1",
            """{"seatId":"00000000-0000-0000-0000-000000000001"}""",
            OccurredAt,
            traceParent);
}
