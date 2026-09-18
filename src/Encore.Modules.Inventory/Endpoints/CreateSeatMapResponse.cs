namespace Encore.Modules.Inventory.Endpoints;

/// <summary>The seats that now exist.</summary>
/// <param name="EventId">The event they belong to.</param>
/// <param name="SeatIds">Their ids, in creation order.</param>
public sealed record CreateSeatMapResponse(Guid EventId, IReadOnlyList<Guid> SeatIds);
