using System.Diagnostics;
using System.Text.Json;
using Encore.Modules.Inventory.Adapters.Messaging;
using Encore.Modules.Inventory.Contracts.Events;
using Encore.Modules.Inventory.Domain.Events;
using Encore.Shared;

namespace Encore.Modules.Inventory.Adapters.Persistence;

/// <summary>
/// Maps a seat's domain events to published contracts and outbox rows, so a rename inside
/// the aggregate never changes the wire format. An unmapped event throws at the first
/// save that raises it rather than being silently dropped.
/// </summary>
internal static class SeatEventPublication
{
    /// <summary>Web defaults (camelCase), matching the HTTP surface.</summary>
    internal static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    /// <summary>Maps one domain event to the row that will publish it.</summary>
    internal static OutboxMessage ToOutboxMessage(IDomainEvent domainEvent) => domainEvent switch
    {
        SeatHeld held => Message(
            InventoryEventTypes.SeatHeld,
            new SeatHeldV1(held.SeatId, held.EventId, held.ClientId, held.HoldExpiresAt, held.OccurredAt),
            held.OccurredAt),

        SeatReleased released => Message(
            InventoryEventTypes.SeatReleased,
            new SeatReleasedV1(
                released.SeatId,
                released.EventId,
                released.ClientId,
                ReasonOf(released.Reason),
                released.OccurredAt),
            released.OccurredAt),

        SeatSold sold => Message(
            InventoryEventTypes.SeatSold,
            new SeatSoldV1(sold.SeatId, sold.EventId, sold.ClientId, sold.OccurredAt),
            sold.OccurredAt),

        _ => throw new NotSupportedException(
            $"No published contract for domain event '{domainEvent.GetType().Name}'. "
            + $"Add one to {nameof(SeatEventPublication)} and register it in "
            + $"{nameof(OutboxEventCatalog)}, or the event will never leave this module.")
    };

    private static OutboxMessage Message<TContract>(
        string eventType,
        TContract contract,
        DateTime occurredAt) =>
        OutboxMessage.For(
            // Version 7: time-ordered, so the consumer's deduplication index stays compact.
            Guid.CreateVersion7(),
            eventType,
            JsonSerializer.Serialize(contract, SerializerOptions),
            occurredAt,
            CurrentTraceParent());

    /// <summary>
    /// The traced operation this save runs inside, if any. Null when nothing is listening,
    /// since then no activity exists to name.
    /// </summary>
    private static string? CurrentTraceParent() =>
        Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity ? activity.Id : null;

    private static string ReasonOf(SeatReleaseReason reason) => reason switch
    {
        SeatReleaseReason.Cancelled => SeatReleasedV1.Cancelled,
        SeatReleaseReason.Expired => SeatReleasedV1.Expired,

        // No catch-all: a new reason must get its own published spelling.
        _ => throw new NotSupportedException(
            $"No published spelling for {nameof(SeatReleaseReason)}.{reason}.")
    };
}
