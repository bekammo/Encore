namespace Encore.Modules.Inventory.Application;

/// <summary>
/// How an attempt to release a seat turned out. A closed set, so an endpoint can
/// switch on it and map every case to a distinct response.
/// </summary>
public enum ReleaseSeatOutcome
{
    /// <summary>
    /// The client no longer holds the seat, including when it was already gone.
    /// </summary>
    Released = 0,

    /// <summary>
    /// Sold to somebody else. Terminal.
    /// </summary>
    AlreadySold = 1,

    /// <summary>Somebody else holds it, so it is not this client's to give up.</summary>
    NotTheHolder = 2,

    /// <summary>No such seat at that event.</summary>
    SeatNotFound = 3,

    /// <summary>
    /// Lost the race twice. Retryable.
    /// </summary>
    LostRace = 4,

    /// <summary>
    /// Sold to this client: their own purchase has gone through.
    /// </summary>
    SoldToYou = 5
}
