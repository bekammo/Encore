namespace Encore.Modules.Inventory.Application;

public enum ReleaseSeatOutcome
{
    Released = 0,
    AlreadySold = 1,
    NotTheHolder = 2,
    SeatNotFound = 3,
    LostRace = 4,
    SoldToYou = 5
}
