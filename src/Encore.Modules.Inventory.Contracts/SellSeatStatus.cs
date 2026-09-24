namespace Encore.Modules.Inventory.Contracts;

public enum SellSeatStatus
{
    AlreadySold = 1,
    NotTheHolder = 2,
    HoldExpired = 3,
    NoActiveHold = 4,
    SeatNotFound = 5,
    LostRace = 6
}
