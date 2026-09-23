using System.Text.Json.Serialization;

namespace Encore.Modules.Inventory.Contracts.Events;

/// <summary>
/// Published when a held seat returns to the pool. <see cref="Reason"/> is a string, not an
/// enum, so adding a reason can never re-label rows already written.
/// </summary>
/// <param name="Reason"><c>"cancelled"</c> or <c>"expired"</c>.</param>
public sealed record SeatReleasedV1(
    [property: JsonPropertyName("seatId")] Guid SeatId,
    [property: JsonPropertyName("eventId")] Guid EventId,
    [property: JsonPropertyName("clientId")] Guid ClientId,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("occurredAt")] DateTime OccurredAt)
{
    public static readonly string Cancelled = "cancelled";

    public static readonly string Expired = "expired";
}
