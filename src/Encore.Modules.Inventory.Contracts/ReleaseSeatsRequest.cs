namespace Encore.Modules.Inventory.Contracts;

public sealed record ReleaseSeatsRequest(Guid EventId, IReadOnlyList<Guid> SeatIds, Guid ClientId);
