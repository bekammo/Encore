namespace Encore.Modules.Inventory.Application;

/// <summary>
/// The result of a <see cref="SellSeatCommand"/>.
/// </summary>
/// <remarks>
/// Deliberately carries no payload beyond the outcome. A sale's interesting
/// detail — who bought it, when — is on the seat row and in
/// <see cref="Domain.Events.SeatSold"/>; repeating it here would be a second copy
/// of the truth that could drift from the first.
/// </remarks>
/// <param name="Outcome">What happened.</param>
public sealed record SellSeatResult(SellSeatOutcome Outcome)
{
    /// <summary>
    /// The seat is the client's. Returned for a sale completed by this call and,
    /// idempotently, for one this client had already completed — a retried or
    /// double-submitted checkout is not a failure to report to the buyer.
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
