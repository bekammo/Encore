namespace Encore.Modules.Inventory.Application;

/// <summary>
/// How an attempt to hold a seat turned out. A closed set, so an endpoint can
/// switch on it and map every case to a distinct response.
/// </summary>
/// <remarks>
/// Only <see cref="Held"/> is a success. The rest are ordinary outcomes rather
/// than faults: under flash-sale load, losing a seat to somebody else is the
/// common path, not the exceptional one, which is why they travel as a return
/// value instead of an exception.
/// </remarks>
public enum HoldSeatOutcome
{
    /// <summary>The client now holds the seat.</summary>
    Held = 0,

    /// <summary>Somebody else holds it and their hold is still live.</summary>
    AlreadyHeld = 1,

    /// <summary>The seat is sold. Terminal — nothing moves it from here.</summary>
    AlreadySold = 2,

    /// <summary>No seat with that id exists.</summary>
    SeatNotFound = 3,

    /// <summary>
    /// The seat changed underneath this attempt twice: once on the first write,
    /// and again after reloading. Rare, and the honest answer is "try again".
    /// </summary>
    LostRace = 4
}
