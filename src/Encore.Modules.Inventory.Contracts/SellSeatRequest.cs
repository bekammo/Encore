namespace Encore.Modules.Inventory.Contracts;

/// <summary>Asks Inventory to convert this client's live hold into a sale.</summary>
/// <param name="EventId">The event the seat is expected to belong to. Checked, never trusted.</param>
/// <param name="SeatId">The seat to sell.</param>
/// <param name="ClientId">Who is buying. Must be the holding client, with an unexpired hold.</param>
public sealed record SellSeatRequest(Guid EventId, Guid SeatId, Guid ClientId);
