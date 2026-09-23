namespace Encore.Modules.Inventory.Application;

/// <summary>
/// The result of a <see cref="SellSeatCommand"/>.
/// </summary>
/// <param name="Outcome">What happened.</param>
public sealed record SellSeatResult(SellSeatOutcome Outcome)
{
    /// <summary>
    /// The seat is the client's, including when it had already been bought by them.
    /// </summary>
    public static SellSeatResult Sold { get; } = new(SellSeatOutcome.Sold);

    /// <summary>Somebody else bought it.</summary>
    public static SellSeatResult AlreadySold { get; } = new(SellSeatOutcome.AlreadySold);

    /// <summary>Somebody else holds it.</summary>
    public static SellSeatResult NotTheHolder { get; } = new(SellSeatOutcome.NotTheHolder);

    /// <summary>This client's hold lapsed before they got here.</summary>
    public static SellSeatResult HoldExpired { get; } = new(SellSeatOutcome.HoldExpired);

    /// <summary>Nobody holds the seat.</summary>
    public static SellSeatResult NoActiveHold { get; } = new(SellSeatOutcome.NoActiveHold);

    /// <summary>No seat with that id exists.</summary>
    public static SellSeatResult SeatNotFound { get; } = new(SellSeatOutcome.SeatNotFound);

    /// <summary>Lost the race twice over. The caller should try again.</summary>
    public static SellSeatResult LostRace { get; } = new(SellSeatOutcome.LostRace);
}
