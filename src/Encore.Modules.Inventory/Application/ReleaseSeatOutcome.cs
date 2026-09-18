namespace Encore.Modules.Inventory.Application;

/// <summary>
/// How an attempt to release a seat turned out. A closed set, so an endpoint can
/// switch on it and map every case to a distinct response.
/// </summary>
public enum ReleaseSeatOutcome
{
    /// <summary>
    /// The client is not holding the seat. Returned both for a hold given up now
    /// and for one that was already gone — see <see cref="ReleaseSeatResult.Released"/>.
    /// </summary>
    Released = 0,

    /// <summary>
    /// The seat is sold, and a sale cannot be undone by releasing. Terminal.
    /// </summary>
    AlreadySold = 1,

    /// <summary>Somebody else holds it, so it is not this client's to give up.</summary>
    NotTheHolder = 2,

    /// <summary>No such seat at that event.</summary>
    SeatNotFound = 3,

    /// <summary>
    /// The seat changed underneath this attempt twice. Rare, and the honest
    /// answer is "try again".
    /// </summary>
    LostRace = 4
}
