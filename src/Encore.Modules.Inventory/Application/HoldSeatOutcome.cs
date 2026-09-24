namespace Encore.Modules.Inventory.Application;

public enum HoldSeatOutcome
{
    Held = 0,
    AlreadyHeld = 1,
    AlreadySold = 2,
    SeatNotFound = 3,
    LostRace = 4,
    HoldCapReached = 5,
    ConcurrentRequestInFlight = 6
}
