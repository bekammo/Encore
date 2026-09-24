namespace Encore.Modules.Inventory.Domain.Exceptions;

public enum SeatTransitionReason
{
    SeatAlreadyHeld = 0,
    SeatAlreadySold = 1,
    NotTheHolder = 2,
    HoldExpired = 3,
    NoActiveHold = 4
}
