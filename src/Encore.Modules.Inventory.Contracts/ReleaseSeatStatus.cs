namespace Encore.Modules.Inventory.Contracts;

/// <summary>Every way a release attempt can end.</summary>
public enum ReleaseSeatStatus
{
    /// <summary>
    /// The seat is available again. Also reported when it was already available,
    /// which is what makes a retried release safe.
    /// </summary>
    Released = 0,

    /// <summary>
    /// The seat has been sold to somebody else, and a sale cannot be undone here.
    /// </summary>
    AlreadySold = 1,

    /// <summary>Somebody else holds this seat, so this client cannot give it back.</summary>
    NotTheHolder = 2,

    /// <summary>No such seat, or not in the event the request named.</summary>
    SeatNotFound = 3,

    /// <summary>Another writer reached the row first. Worth retrying.</summary>
    LostRace = 4,

    /// <summary>
    /// Sold to the client asking to release it. A caller unwinding an order uses this to
    /// see that the sale it is racing is its own, so the money must stay.
    /// </summary>
    SoldToYou = 5
}
