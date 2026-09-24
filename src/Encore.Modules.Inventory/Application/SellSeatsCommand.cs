namespace Encore.Modules.Inventory.Application;

public sealed record SellSeatsCommand(Guid EventId, IReadOnlyList<Guid> SeatIds, Guid ClientId);
