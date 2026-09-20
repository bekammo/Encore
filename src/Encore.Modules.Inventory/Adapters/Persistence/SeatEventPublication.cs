using System.Text.Json;
using Encore.Modules.Inventory.Adapters.Messaging;
using Encore.Modules.Inventory.Contracts.Events;
using Encore.Modules.Inventory.Domain.Events;
using Encore.Shared;

namespace Encore.Modules.Inventory.Adapters.Persistence;

/// <summary>
/// Turns a seat's domain events into outbox rows: the one place that knows both
/// the domain's vocabulary and the published one.
/// </summary>
/// <remarks>
/// <para>
/// <b>This translation is the reason the two sets of records exist</b>, and it is
/// the same duty <c>EfSeatRepository</c> performs for
/// <c>DbUpdateConcurrencyException</c> under 003. Inside the module an event is a
/// <c>SeatSold</c> with whatever shape the aggregate finds convenient; outside it is
/// <c>inventory.seat.sold.v1</c> with a shape consumers have been promised. Skipping
/// the step and serialising the domain record would work perfectly until the first
/// rename, at which point rows already written would describe fields that no longer
/// exist.
/// </para>
/// <para>
/// <b>An unmapped domain event throws, and that is the point of the default arm.</b>
/// A fourth <c>IDomainEvent</c> added to the aggregate with no entry here fails the
/// very first save that raises it, loudly, in development. The alternative is that
/// it is silently dropped — an event that was raised, never published, and gone
/// forever, which is exactly the failure an outbox exists to prevent. 008 makes the
/// same call about unhandled refusal reasons: better a loud failure now than a
/// plausible wrong answer later.
/// </para>
/// <para>
/// <b>The reason enum is translated to a string here, not on the wire by accident.</b>
/// See <see cref="SeatReleasedV1"/>: an enum crossing a boundary is an integer, and
/// an integer is only meaningful while both ends agree on member order.
/// </para>
/// </remarks>
internal static class SeatEventPublication
{
    /// <summary>
    /// Web defaults, so the payload reads the way every other JSON this system
    /// emits does — camelCase, and case-insensitive coming back. A stored payload
    /// is queried by hand when something is stuck, so matching the HTTP surface
    /// means one spelling to remember rather than two.
    /// </summary>
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
            // Version 7: random enough to be an identity, time-ordered enough that
            // consecutive ids land near each other in the consumer's deduplication
            // index instead of scattering across it.
            Guid.CreateVersion7(),
            eventType,
            JsonSerializer.Serialize(contract, SerializerOptions),
            occurredAt);

    private static string ReasonOf(SeatReleaseReason reason) => reason switch
    {
        SeatReleaseReason.Cancelled => SeatReleasedV1.Cancelled,
        SeatReleaseReason.Expired => SeatReleasedV1.Expired,

        // No catch-all. A new reason is a new thing to tell a customer, and
        // publishing it as one of the two that already exist would be a lie that
        // no test could catch.
        _ => throw new NotSupportedException(
            $"No published spelling for {nameof(SeatReleaseReason)}.{reason}.")
    };
}
