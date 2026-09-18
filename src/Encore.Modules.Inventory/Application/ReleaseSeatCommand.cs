namespace Encore.Modules.Inventory.Application;

/// <summary>
/// A request to give a held seat back, at the holding client's own request.
/// </summary>
/// <param name="EventId">The event the seat belongs to. Checked, never trusted.</param>
/// <param name="SeatId">The seat being given up.</param>
/// <param name="ClientId">Who is giving it up. Must be the client currently holding it.</param>
public sealed record ReleaseSeatCommand(Guid EventId, Guid SeatId, Guid ClientId);
