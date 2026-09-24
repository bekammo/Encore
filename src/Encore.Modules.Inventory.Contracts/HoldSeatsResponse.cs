namespace Encore.Modules.Inventory.Contracts;

/// <summary>The outcome of holding several seats: one answer per seat.</summary>
/// <param name="Seats">One entry per requested seat, in the order they were asked for.</param>
public sealed record HoldSeatsResponse(IReadOnlyList<HoldSeatResponse> Seats);

/// <summary>The outcome of one seat's hold attempt.</summary>
/// <param name="SeatId">The seat.</param>
/// <param name="Status">What happened. Callers switch over this.</param>
/// <param name="HoldExpiresAt">
/// When the hold lapses, always UTC. Present only when <paramref name="Status"/>
/// is <see cref="HoldSeatStatus.Held"/>; null otherwise.
/// </param>
public sealed record HoldSeatResponse(Guid SeatId, HoldSeatStatus Status, DateTime? HoldExpiresAt = null);
