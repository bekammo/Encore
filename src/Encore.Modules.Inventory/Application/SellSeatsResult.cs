namespace Encore.Modules.Inventory.Application;

public sealed record SellSeatsResult(IReadOnlyList<SeatSaleRefusal> Refusals)
{
    public static SellSeatsResult Sold { get; } = new([]);

    public bool AllSold => Refusals.Count is 0;
}

public sealed record SeatSaleRefusal(Guid SeatId, SellSeatOutcome Outcome);
