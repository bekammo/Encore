namespace Encore.Modules.Inventory.Application;

/// <summary>
/// A request to put one seat in one client's basket.
/// </summary>
/// <remarks>
/// No expiry or timestamp: the aggregate owns the duration and the handler owns the clock.
/// </remarks>
/// <param name="EventId">The event the seat belongs to.</param>
/// <param name="SeatId">The seat being claimed.</param>
/// <param name="ClientId">Who is claiming it.</param>
public sealed record HoldSeatCommand(Guid EventId, Guid SeatId, Guid ClientId);
