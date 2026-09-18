namespace Encore.Modules.Inventory.Application;

/// <summary>
/// The result of a <see cref="HoldSeatCommand"/>: an <see cref="HoldSeatOutcome"/>,
/// plus the expiry when — and only when — a hold was actually taken.
/// </summary>
/// <remarks>
/// Constructed through the static members rather than the constructor, so that
/// "only a successful hold carries an expiry" is a fact of the type rather than
/// a convention every call site has to remember.
/// </remarks>
/// <param name="Outcome">What happened.</param>
/// <param name="HoldExpiresAt">
/// When the new hold lapses. Non-null exactly when <paramref name="Outcome"/> is
/// <see cref="HoldSeatOutcome.Held"/>.
/// </param>
public sealed record HoldSeatResult(HoldSeatOutcome Outcome, DateTime? HoldExpiresAt = null)
{
    /// <summary>The client took the seat, and the hold lapses at <paramref name="holdExpiresAt"/>.</summary>
    public static HoldSeatResult Held(DateTime holdExpiresAt) =>
        new(HoldSeatOutcome.Held, holdExpiresAt);

    /// <summary>Somebody else holds it, and their hold is still live.</summary>
    public static HoldSeatResult AlreadyHeld { get; } = new(HoldSeatOutcome.AlreadyHeld);

    /// <summary>The seat is sold.</summary>
    public static HoldSeatResult AlreadySold { get; } = new(HoldSeatOutcome.AlreadySold);

    /// <summary>No seat with that id exists.</summary>
    public static HoldSeatResult SeatNotFound { get; } = new(HoldSeatOutcome.SeatNotFound);

    /// <summary>Lost the race twice over. The caller should try again.</summary>
    public static HoldSeatResult LostRace { get; } = new(HoldSeatOutcome.LostRace);

    /// <summary>The client is already at their hold cap for this event.</summary>
    public static HoldSeatResult HoldCapReached { get; } = new(HoldSeatOutcome.HoldCapReached);
}
