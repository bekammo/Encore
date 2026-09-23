using System.Text.Json.Serialization;

namespace Encore.Modules.Inventory.Contracts.Events;

/// <summary>
/// Published when a seat is claimed. A contract separate from the domain event, so a
/// rename inside <c>Seat</c> is never a breaking change for consumers. The wire names are
/// spelled here, so a consumer holding only this assembly reads what Inventory writes.
/// </summary>
/// <param name="SeatId">The seat that was claimed.</param>
/// <param name="EventId">The concert the seat belongs to.</param>
/// <param name="ClientId">Who claimed it.</param>
/// <param name="HoldExpiresAt">When this claim lapses if nothing else happens. UTC.</param>
/// <param name="OccurredAt">When the claim was made. UTC.</param>
public sealed record SeatHeldV1(
    [property: JsonPropertyName("seatId")] Guid SeatId,
    [property: JsonPropertyName("eventId")] Guid EventId,
    [property: JsonPropertyName("clientId")] Guid ClientId,
    [property: JsonPropertyName("holdExpiresAt")] DateTime HoldExpiresAt,
    [property: JsonPropertyName("occurredAt")] DateTime OccurredAt);
