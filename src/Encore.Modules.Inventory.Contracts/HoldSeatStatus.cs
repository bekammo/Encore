namespace Encore.Modules.Inventory.Contracts;

public enum HoldSeatStatus
{
    Held = 0,

    AlreadyHeld = 1,

    AlreadySold = 2,

    SeatNotFound = 3,

    LostRace = 4,

    HoldCapReached = 5,

    /// <summary>
    /// Another request by this client for the event was in flight (005); the seat was not judged.
    /// </summary>
    ConcurrentRequestInFlight = 6
}
