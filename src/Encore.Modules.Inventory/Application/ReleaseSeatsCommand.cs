namespace Encore.Modules.Inventory.Application;

public sealed record ReleaseSeatsCommand(Guid EventId, IReadOnlyList<Guid> SeatIds, Guid ClientId);
