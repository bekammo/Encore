namespace Encore.Modules.Inventory.Application;

/// <summary>
/// A request to convert one client's live hold into a confirmed sale.
/// </summary>
/// <param name="EventId">The event the seat belongs to.</param>
/// <param name="SeatId">The seat being bought.</param>
/// <param name="ClientId">Who is buying it. Must be the client currently holding it.</param>
public sealed record SellSeatCommand(Guid EventId, Guid SeatId, Guid ClientId);
