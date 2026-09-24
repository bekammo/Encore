namespace Encore.Modules.Inventory.Application;

public enum SellSeatOutcome
{
    Sold = 0,
    AlreadySold = 1,
    NotTheHolder = 2,
    HoldExpired = 3,
    NoActiveHold = 4,
    SeatNotFound = 5,
    LostRace = 6
}
