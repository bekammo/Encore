namespace Encore.Modules.Inventory.Application;

/// <summary>
/// The result of a <see cref="ReleaseSeatCommand"/>.
/// </summary>
/// <param name="Outcome">What happened.</param>
public sealed record ReleaseSeatResult(ReleaseSeatOutcome Outcome)
{
    /// <summary>
    /// The client no longer holds the seat, including when the hold had already lapsed.
    /// </summary>
    public static ReleaseSeatResult Released { get; } = new(ReleaseSeatOutcome.Released);

    /// <summary>The seat is sold to somebody else; releasing is not a way out of that.</summary>
    public static ReleaseSeatResult AlreadySold { get; } = new(ReleaseSeatOutcome.AlreadySold);

    /// <summary>
    /// Sold to this client. A cancel racing its own confirm uses this to back off.
    /// </summary>
    public static ReleaseSeatResult SoldToYou { get; } = new(ReleaseSeatOutcome.SoldToYou);

    /// <summary>Somebody else holds it.</summary>
    public static ReleaseSeatResult NotTheHolder { get; } = new(ReleaseSeatOutcome.NotTheHolder);

    /// <summary>No such seat at that event.</summary>
    public static ReleaseSeatResult SeatNotFound { get; } = new(ReleaseSeatOutcome.SeatNotFound);

    /// <summary>Lost the race twice over. The caller should try again.</summary>
    public static ReleaseSeatResult LostRace { get; } = new(ReleaseSeatOutcome.LostRace);
}
