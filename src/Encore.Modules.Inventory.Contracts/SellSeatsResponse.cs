namespace Encore.Modules.Inventory.Contracts;

/// <summary>
/// A refusal names only the seats that refused; the rest of the request did not sell either.
/// </summary>
/// <param name="Refusals">
/// Empty exactly when every seat is sold to this client, including by an earlier call, so a
/// resubmitted checkout is not told its own purchase failed.
/// </param>
public sealed record SellSeatsResponse(IReadOnlyList<SellSeatResponse> Refusals)
{
    public bool AllSold => Refusals.Count is 0;
}

public sealed record SellSeatResponse(Guid SeatId, SellSeatStatus Status);
