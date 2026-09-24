namespace Encore.Modules.Inventory.Contracts;

public sealed record ReleaseSeatsResponse(IReadOnlyList<ReleaseSeatResponse> Seats);

public sealed record ReleaseSeatResponse(Guid SeatId, ReleaseSeatStatus Status);
