namespace Encore.Modules.Inventory.Application;

public sealed record HoldSeatsCommand(Guid EventId, IReadOnlyList<Guid> SeatIds, Guid ClientId);
