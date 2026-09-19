namespace Encore.Modules.Inventory.Contracts;

/// <summary>Asks Inventory to give a held seat back.</summary>
/// <param name="EventId">The event the seat is expected to belong to. Checked, never trusted.</param>
/// <param name="SeatId">The seat to release.</param>
/// <param name="ClientId">Who is asking. Only the holding client may release.</param>
public sealed record ReleaseSeatRequest(Guid EventId, Guid SeatId, Guid ClientId);
