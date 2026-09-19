namespace Encore.Modules.Inventory.Contracts;

/// <summary>Every way a sale attempt can end.</summary>
public enum SellSeatStatus
{
    /// <summary>
    /// The seat belongs to this client. Also reported when this client had
    /// already bought it, so a resubmitted checkout is not told its own
    /// completed purchase failed.
    /// </summary>
    Sold = 0,

    /// <summary>Somebody else bought it.</summary>
    AlreadySold = 1,

    /// <summary>Somebody else holds it, so there is nothing here to convert.</summary>
    NotTheHolder = 2,

    /// <summary>This client's hold lapsed before the sale was attempted.</summary>
    HoldExpired = 3,

    /// <summary>The seat is available. A sale must pass through a hold first.</summary>
    NoActiveHold = 4,

    /// <summary>No such seat, or not in the event the request named.</summary>
    SeatNotFound = 5,

    /// <summary>Another writer reached the row first. Worth retrying.</summary>
    LostRace = 6
}
