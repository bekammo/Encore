using Encore.Modules.Inventory.Contracts;

namespace Encore.Modules.Orders;

/// <summary>
/// One seat a checkout could not get, and Inventory's own word for why.
/// </summary>
/// <param name="SeatId">The seat that was refused.</param>
/// <param name="Status">Inventory's reason. Never <see cref="HoldSeatStatus.Held"/>.</param>
public sealed record SeatRefusal(Guid SeatId, HoldSeatStatus Status);
