namespace Encore.Modules.Inventory.Application;

public sealed record HoldSeatCommand(Guid EventId, Guid SeatId, Guid ClientId);
