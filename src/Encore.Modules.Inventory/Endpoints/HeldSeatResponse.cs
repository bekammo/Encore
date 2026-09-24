namespace Encore.Modules.Inventory.Endpoints;

public sealed record HeldSeatResponse(Guid SeatId, DateTime HoldExpiresAt);
