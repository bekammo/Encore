namespace Encore.Modules.Inventory.Contracts;

/// <summary>Every way a hold attempt can end. A closed set, so callers can switch exhaustively.</summary>
public enum HoldSeatStatus
{
    /// <summary>The seat is now held by the requesting client.</summary>
    Held = 0,

    /// <summary>Somebody else holds it, and their hold has not lapsed.</summary>
    AlreadyHeld = 1,

    /// <summary>The seat has been sold. Terminal — no later attempt will succeed.</summary>
    AlreadySold = 2,

    /// <summary>No such seat, or not in the event the request named.</summary>
    SeatNotFound = 3,

    /// <summary>Another writer reached the row first. Worth retrying.</summary>
    LostRace = 4,

    /// <summary>This client already holds as many seats for this event as it may.</summary>
    HoldCapReached = 5,

    /// <summary>
    /// Another request from this same client was mid-flight. Worth retrying —
    /// this is the system refusing, not the seat.
    /// </summary>
    ConcurrentRequestInFlight = 6
}
