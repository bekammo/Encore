using System.Text.Json.Serialization;

namespace Encore.Modules.Inventory.Contracts.Events;

public sealed record SeatHeldV1(
    [property: JsonPropertyName("seatId")] Guid SeatId,
    [property: JsonPropertyName("eventId")] Guid EventId,
    [property: JsonPropertyName("clientId")] Guid ClientId,
    [property: JsonPropertyName("holdExpiresAt")] DateTime HoldExpiresAt,
    [property: JsonPropertyName("occurredAt")] DateTime OccurredAt);
