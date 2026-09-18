namespace Encore.Modules.Inventory.Application;

/// <summary>
/// A request to convert one client's live hold into a confirmed sale.
/// </summary>
/// <param name="SeatId">The seat being bought.</param>
/// <param name="ClientId">Who is buying it. Must be the client currently holding it.</param>
public sealed record SellSeatCommand(Guid SeatId, Guid ClientId);
