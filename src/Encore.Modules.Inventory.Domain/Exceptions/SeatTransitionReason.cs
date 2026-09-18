namespace Encore.Modules.Inventory.Domain.Exceptions;

/// <summary>
/// Why a seat refused a state transition. A closed set, so callers can switch on
/// it and map each case to a distinct outcome for the client.
/// </summary>
public enum SeatTransitionReason
{
    /// <summary>Someone else holds the seat and their hold is still live.</summary>
    SeatAlreadyHeld = 0,

    /// <summary>The seat is sold. Terminal — nothing moves it from here.</summary>
    SeatAlreadySold = 1,

    /// <summary>The caller is not the client currently holding the seat.</summary>
    NotTheHolder = 2,

    /// <summary>The caller's own hold lapsed before they got here.</summary>
    HoldExpired = 3,

    /// <summary>The operation needs a live hold and there is none.</summary>
    NoActiveHold = 4
}
