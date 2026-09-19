using Encore.Modules.Inventory.Contracts;

namespace Encore.Modules.Orders;

/// <summary>
/// One seat a checkout could not get, and Inventory's own word for why.
/// </summary>
/// <remarks>
/// The status is carried through unchanged rather than translated into Orders'
/// vocabulary. Inventory owns the question "why can you not have this seat" and
/// its enum is already a closed, published set; restating it here would be a
/// second copy that drifts the first time a case is added.
/// </remarks>
/// <param name="SeatId">The seat that was refused.</param>
/// <param name="Status">Inventory's reason. Never <see cref="HoldSeatStatus.Held"/>.</param>
public sealed record SeatRefusal(Guid SeatId, HoldSeatStatus Status);
