namespace Encore.Modules.Inventory.Application;

public sealed record ReleaseSeatCommand(Guid EventId, Guid SeatId, Guid ClientId);
