namespace Encore.Modules.Inventory.Endpoints;

/// <summary>
/// A successful sale or release. Carries the seat and its resulting status and
/// nothing else — who bought it and when lives on the seat row and in the
/// domain events, and repeating it here would be a second copy of the truth
/// that could drift from the first.
/// </summary>
/// <param name="SeatId">The seat acted on.</param>
/// <param name="Status">Its status now, as a lowercase string.</param>
public sealed record SeatActionResponse(Guid SeatId, string Status);
