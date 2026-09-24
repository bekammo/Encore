namespace Encore.Modules.Inventory.Application;

public sealed record SellSeatCommand(Guid EventId, Guid SeatId, Guid ClientId);
