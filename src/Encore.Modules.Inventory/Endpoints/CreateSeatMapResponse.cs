namespace Encore.Modules.Inventory.Endpoints;

public sealed record CreateSeatMapResponse(Guid EventId, IReadOnlyList<Guid> SeatIds);
