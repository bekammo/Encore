namespace Encore.Modules.Inventory.Endpoints;

/// <summary>
/// A successful sale or release: the seat and its resulting status.
/// </summary>
/// <param name="SeatId">The seat acted on.</param>
/// <param name="Status">Its status now, as a lowercase string.</param>
public sealed record SeatActionResponse(Guid SeatId, string Status);
