namespace Encore.Modules.Inventory.Application;

/// <summary>
/// The result of a <see cref="HoldSeatCommand"/>: an <see cref="HoldSeatOutcome"/>,
/// plus the expiry when — and only when — a hold was actually taken.
/// </summary>
/// <param name="Outcome">What happened; each case is described on <see cref="HoldSeatOutcome"/>.</param>
/// <param name="HoldExpiresAt">
/// When the new hold lapses. Non-null exactly when <paramref name="Outcome"/> is
/// <see cref="HoldSeatOutcome.Held"/>.
/// </param>
public sealed record HoldSeatResult(HoldSeatOutcome Outcome, DateTime? HoldExpiresAt = null)
{
    /// <summary>The client took the seat, and the hold lapses at <paramref name="holdExpiresAt"/>.</summary>
    public static HoldSeatResult Held(DateTime holdExpiresAt) =>
        new(HoldSeatOutcome.Held, holdExpiresAt);

    /// <summary><see cref="HoldSeatOutcome.AlreadyHeld"/>.</summary>
    public static HoldSeatResult AlreadyHeld { get; } = new(HoldSeatOutcome.AlreadyHeld);

    /// <summary><see cref="HoldSeatOutcome.AlreadySold"/>.</summary>
    public static HoldSeatResult AlreadySold { get; } = new(HoldSeatOutcome.AlreadySold);

    /// <summary><see cref="HoldSeatOutcome.SeatNotFound"/>.</summary>
    public static HoldSeatResult SeatNotFound { get; } = new(HoldSeatOutcome.SeatNotFound);

    /// <summary><see cref="HoldSeatOutcome.LostRace"/>.</summary>
    public static HoldSeatResult LostRace { get; } = new(HoldSeatOutcome.LostRace);

    /// <summary><see cref="HoldSeatOutcome.HoldCapReached"/>.</summary>
    public static HoldSeatResult HoldCapReached { get; } = new(HoldSeatOutcome.HoldCapReached);

    /// <summary><see cref="HoldSeatOutcome.ConcurrentRequestInFlight"/>.</summary>
    public static HoldSeatResult ConcurrentRequestInFlight { get; } =
        new(HoldSeatOutcome.ConcurrentRequestInFlight);
}
