namespace Encore.Modules.Inventory.Contracts;

public sealed record HoldSeatsResponse(IReadOnlyList<HoldSeatResponse> Seats);

/// <param name="HoldExpiresAt">
/// Non-null exactly when <paramref name="Status"/> is <see cref="HoldSeatStatus.Held"/>.
/// </param>
public sealed record HoldSeatResponse(Guid SeatId, HoldSeatStatus Status, DateTime? HoldExpiresAt = null);
