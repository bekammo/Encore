namespace Encore.Modules.Inventory.Contracts;

/// <summary>
/// The outcome of selling several seats: all of them, or the reasons none were.
/// </summary>
/// <remarks>
/// There is no partial sale to describe. A refusal names only the seats that
/// refused; every other seat in the request was not sold either.
/// </remarks>
/// <param name="Refusals">Why the sale did not happen. Empty exactly when it did.</param>
public sealed record SellSeatsResponse(IReadOnlyList<SellSeatResponse> Refusals)
{
    /// <summary>Whether every seat now belongs to the client.</summary>
    public bool AllSold => Refusals.Count is 0;
}

/// <summary>One seat's answer to a sale.</summary>
/// <param name="SeatId">The seat.</param>
/// <param name="Status">What that seat said. Callers switch over this.</param>
public sealed record SellSeatResponse(Guid SeatId, SellSeatStatus Status);
