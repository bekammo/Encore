namespace Encore.Modules.Inventory.Contracts;

public sealed record HoldSeatsRequest(Guid EventId, IReadOnlyList<Guid> SeatIds, Guid ClientId);
