namespace Encore.Modules.Inventory.Contracts;

/// <summary>The outcome of releasing several seats: one answer per seat.</summary>
/// <param name="Seats">One entry per requested seat, in the order they were asked for.</param>
public sealed record ReleaseSeatsResponse(IReadOnlyList<ReleaseSeatResponse> Seats);

/// <summary>The outcome of one seat's release attempt.</summary>
/// <param name="SeatId">The seat.</param>
/// <param name="Status">What happened. Callers switch over this.</param>
public sealed record ReleaseSeatResponse(Guid SeatId, ReleaseSeatStatus Status);
