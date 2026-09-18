namespace Encore.Modules.Inventory.Application;

/// <summary>
/// How an attempt to sell a seat turned out. A closed set, so an endpoint can
/// switch on it and map every case to a distinct response.
/// </summary>
public enum SellSeatOutcome
{
    /// <summary>
    /// The seat belongs to the client. Returned both for a sale made now and for
    /// one this client had already made — see <see cref="SellSeatResult.Sold"/>.
    /// </summary>
    Sold = 0,

    /// <summary>Somebody else bought it. Terminal, and not this client's seat.</summary>
    AlreadySold = 1,

    /// <summary>Somebody else holds it, so it is not this client's to buy.</summary>
    NotTheHolder = 2,

    /// <summary>This client's own hold lapsed before checkout completed.</summary>
    HoldExpired = 3,

    /// <summary>Nobody holds the seat. A sale has to go through a hold first.</summary>
    NoActiveHold = 4,

    /// <summary>No seat with that id exists.</summary>
    SeatNotFound = 5,

    /// <summary>
    /// The seat changed underneath this attempt twice: once on the first write,
    /// and again after reloading. Rare, and the honest answer is "try again".
    /// </summary>
    LostRace = 6
}
