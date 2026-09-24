using System.Text.Json.Serialization;

namespace Encore.Modules.Inventory.Contracts.Events;

/// <summary>
/// <see cref="Reason"/> is a string, not an enum, so adding a reason can never re-label rows
/// already written.
/// </summary>
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
