namespace Encore.Modules.Inventory.Contracts;

/// <summary>
/// Asks Inventory to convert several of this client's live holds into sales —
/// every one of them, or none.
/// </summary>
/// <param name="EventId">The event every seat is expected to belong to. Checked, never trusted.</param>
/// <param name="SeatIds">The seats to buy, distinct.</param>
/// <param name="ClientId">Who is buying. Must hold every seat, with an unexpired hold.</param>
public sealed record SellSeatsRequest(Guid EventId, IReadOnlyList<Guid> SeatIds, Guid ClientId);
