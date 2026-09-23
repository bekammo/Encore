using System.Text.Json.Serialization;

namespace Encore.Modules.Inventory.Contracts.Events;

/// <summary>
/// Published when a held seat has been converted to a confirmed sale. Terminal —
/// no later event ever moves this seat back.
/// </summary>
/// <param name="SeatId">The seat that was sold.</param>
/// <param name="EventId">The concert the seat belongs to.</param>
/// <param name="ClientId">Who bought it.</param>
/// <param name="OccurredAt">When the sale was confirmed. UTC.</param>
public sealed record SeatSoldV1(
    [property: JsonPropertyName("seatId")] Guid SeatId,
    [property: JsonPropertyName("eventId")] Guid EventId,
    [property: JsonPropertyName("clientId")] Guid ClientId,
    [property: JsonPropertyName("occurredAt")] DateTime OccurredAt);
