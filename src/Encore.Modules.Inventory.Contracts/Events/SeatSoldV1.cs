using System.Text.Json.Serialization;

namespace Encore.Modules.Inventory.Contracts.Events;

/// <summary>Terminal: no later event moves this seat back.</summary>
public sealed record SeatSoldV1(
    [property: JsonPropertyName("seatId")] Guid SeatId,
    [property: JsonPropertyName("eventId")] Guid EventId,
    [property: JsonPropertyName("clientId")] Guid ClientId,
    [property: JsonPropertyName("occurredAt")] DateTime OccurredAt);
