namespace Encore.Modules.Orders;

public enum CheckoutOutcome
{
    Created = 0,
    NoSeats = 1,
    DuplicateSeat = 2,
    TooManySeats = 3,
    EventNotFound = 4,
    NotOnSale = 5,
    CheckoutAlreadyOpen = 6,
    SeatsUnavailable = 7
}
