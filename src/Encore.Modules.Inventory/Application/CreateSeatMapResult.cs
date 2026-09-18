namespace Encore.Modules.Inventory.Application;

/// <summary>
/// The result of a <see cref="CreateSeatMapCommand"/>: the ids of the seats
/// that now exist.
/// </summary>
/// <remarks>
/// The ids are returned rather than left to be discovered, because the caller
/// has no other way to learn them — seats have no natural key, and a client that
/// has just created a seat map needs something to hold.
/// </remarks>
/// <param name="SeatIds">The newly created seats, in creation order.</param>
public sealed record CreateSeatMapResult(IReadOnlyList<Guid> SeatIds);
