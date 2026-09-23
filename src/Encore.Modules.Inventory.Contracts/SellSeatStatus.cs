namespace Encore.Modules.Inventory.Contracts;

/// <summary>
/// Every way a sale can be refused. A sale that succeeds reports no refusal at all, which is
/// also the answer when this client had already bought every seat, so a resubmitted checkout
/// is not told its own completed purchase failed.
/// </summary>
public enum SellSeatStatus
{
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
