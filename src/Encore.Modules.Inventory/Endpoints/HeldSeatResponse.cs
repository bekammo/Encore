namespace Encore.Modules.Inventory.Endpoints;

/// <summary>A successful hold.</summary>
/// <param name="SeatId">The seat now held.</param>
/// <param name="HoldExpiresAt">
/// When the hold lapses, in UTC. The caller has until then to complete checkout.
/// </param>
public sealed record HeldSeatResponse(Guid SeatId, DateTime HoldExpiresAt);
