namespace Encore.Modules.Inventory.Application;

public sealed record HoldSeatResult(HoldSeatOutcome Outcome, DateTime? HoldExpiresAt = null)
{
    public static HoldSeatResult Held(DateTime holdExpiresAt) =>
        new(HoldSeatOutcome.Held, holdExpiresAt);

    public static HoldSeatResult AlreadyHeld { get; } = new(HoldSeatOutcome.AlreadyHeld);

    public static HoldSeatResult AlreadySold { get; } = new(HoldSeatOutcome.AlreadySold);

    public static HoldSeatResult SeatNotFound { get; } = new(HoldSeatOutcome.SeatNotFound);

    public static HoldSeatResult LostRace { get; } = new(HoldSeatOutcome.LostRace);

    public static HoldSeatResult HoldCapReached { get; } = new(HoldSeatOutcome.HoldCapReached);

    public static HoldSeatResult ConcurrentRequestInFlight { get; } =
        new(HoldSeatOutcome.ConcurrentRequestInFlight);
}
