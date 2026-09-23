namespace Encore.Modules.Inventory.Application;

/// <summary>
/// The result of a <see cref="ReleaseSeatCommand"/>.
/// </summary>
/// <param name="Outcome">What happened.</param>
public sealed record ReleaseSeatResult(ReleaseSeatOutcome Outcome)
{
    /// <summary>
    /// The client is no longer holding the seat. Returned for a hold given up by
    /// this call and, idempotently, for one that had already lapsed or had never
    /// been taken — the caller asked not to be holding this seat, and they are
    /// not. Reporting failure for a state that already holds would turn a retry
    /// into an error.
    /// </summary>
    public static ReleaseSeatResult Released { get; } = new(ReleaseSeatOutcome.Released);

    /// <summary>The seat is sold to somebody else; releasing is not a way out of that.</summary>
    public static ReleaseSeatResult AlreadySold { get; } = new(ReleaseSeatOutcome.AlreadySold);

    /// <summary>
    /// The seat is sold to this client. Still a refusal — a sale is not undone by
    /// releasing — but one that says the client's own purchase completed, which is
    /// what an order being cancelled mid-confirm needs to hear before it gives
    /// any money back (<c>DECISIONS.md</c> 077).
    /// </summary>
    public static ReleaseSeatResult SoldToYou { get; } = new(ReleaseSeatOutcome.SoldToYou);

    /// <summary>Somebody else holds it.</summary>
    public static ReleaseSeatResult NotTheHolder { get; } = new(ReleaseSeatOutcome.NotTheHolder);

    /// <summary>No such seat at that event.</summary>
    public static ReleaseSeatResult SeatNotFound { get; } = new(ReleaseSeatOutcome.SeatNotFound);

    /// <summary>Lost the race twice over. The caller should try again.</summary>
    public static ReleaseSeatResult LostRace { get; } = new(ReleaseSeatOutcome.LostRace);
}
