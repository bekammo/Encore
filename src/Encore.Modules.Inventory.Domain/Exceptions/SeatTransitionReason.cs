namespace Encore.Modules.Inventory.Domain.Exceptions;

/// <summary>Why a seat refused a transition. A closed set.</summary>
public enum SeatTransitionReason
{
    /// <summary>Someone else holds the seat and their hold is live.</summary>
    SeatAlreadyHeld = 0,

    SeatAlreadySold = 1,

    /// <summary>The caller is not the current holder.</summary>
    NotTheHolder = 2,

    /// <summary>The caller's own hold lapsed.</summary>
    HoldExpired = 3,

    /// <summary>The operation needs a live hold and there is none.</summary>
    NoActiveHold = 4
}
