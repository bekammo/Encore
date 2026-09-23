namespace Encore.Modules.Inventory.Application;

/// <summary>
/// The result of a <see cref="CreateSeatMapCommand"/>: the ids of the seats
/// that now exist.
/// </summary>
/// <param name="SeatIds">The newly created seats, in creation order.</param>
public sealed record CreateSeatMapResult(IReadOnlyList<Guid> SeatIds);
