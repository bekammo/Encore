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

    /// <summary>
    /// No such seat at that event. Covers both "no seat with that id" and "that
    /// seat belongs to a different event" — from the caller's side those are the
    /// same mistake, and distinguishing them would let anyone probe which seat
    /// ids exist by asking about an event they are not looking at.
    /// </summary>
    SeatNotFound = 3,

    /// <summary>
    /// The seat changed underneath this attempt twice: once on the first write,
    /// and again after reloading. Rare, and the honest answer is "try again".
    /// </summary>
    LostRace = 4,

    /// <summary>
    /// The client already holds the most seats they may hold at this event
    /// (<c>DECISIONS.md</c> 006). They must release one or complete checkout
    /// before taking another. Unlike every other refusal here, this one is a
    /// policy rather than an invariant, and is enforced best-effort: with Redis
    /// unavailable a client can slip past it.
    /// </summary>
    HoldCapReached = 5
}
