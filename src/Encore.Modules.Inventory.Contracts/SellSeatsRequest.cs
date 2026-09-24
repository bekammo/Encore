namespace Encore.Modules.Inventory.Contracts;

public sealed record SellSeatsRequest(Guid EventId, IReadOnlyList<Guid> SeatIds, Guid ClientId);
