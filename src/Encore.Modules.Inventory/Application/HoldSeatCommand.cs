namespace Encore.Modules.Inventory.Application;

/// <summary>
/// A request to put one seat in one client's basket.
/// </summary>
/// <remarks>
/// Carries no expiry and no timestamp. How long a hold lasts is the aggregate's
/// rule, and what "now" is comes from the handler's clock — a caller that could
/// supply either would be able to grant itself a hold the rules never approved.
/// </remarks>
/// <param name="SeatId">The seat being claimed.</param>
/// <param name="ClientId">Who is claiming it.</param>
public sealed record HoldSeatCommand(Guid SeatId, Guid ClientId);
