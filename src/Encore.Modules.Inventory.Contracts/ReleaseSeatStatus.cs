namespace Encore.Modules.Inventory.Contracts;

public enum ReleaseSeatStatus
{
    /// <summary>Also answered for a seat already available, so a retried release is safe.</summary>
    Released = 0,

    AlreadySold = 1,

    NotTheHolder = 2,

    SeatNotFound = 3,

    LostRace = 4,

    SoldToYou = 5
}
