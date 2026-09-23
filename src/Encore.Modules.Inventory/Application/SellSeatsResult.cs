namespace Encore.Modules.Inventory.Application;

/// <summary>
/// The result of a <see cref="SellSeatsCommand"/>: every seat sold, or the
/// reasons none were.
/// </summary>
/// <param name="Refusals">Why the sale did not happen. Empty exactly when it did.</param>
public sealed record SellSeatsResult(IReadOnlyList<SeatSaleRefusal> Refusals)
{
    /// <summary>Every seat is the client's.</summary>
    public static SellSeatsResult Sold { get; } = new([]);

    /// <summary>Whether every seat was sold.</summary>
    public bool AllSold => Refusals.Count is 0;
}

/// <summary>One seat's reason for refusing a sale.</summary>
/// <param name="SeatId">The seat.</param>
/// <param name="Outcome">Why. Never <see cref="SellSeatOutcome.Sold"/>.</param>
public sealed record SeatSaleRefusal(Guid SeatId, SellSeatOutcome Outcome);
