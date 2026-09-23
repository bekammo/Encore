namespace Encore.Modules.Inventory.Application;

/// <summary>
/// How a hold attempt turned out. A closed set; refusals are ordinary outcomes under
/// load, so they are returned rather than thrown.
/// </summary>
public enum HoldSeatOutcome
{
    Held = 0,

    /// <summary>Somebody else holds it.</summary>
    AlreadyHeld = 1,

    AlreadySold = 2,

    /// <summary>No such seat at that event. A seat under another event looks the same, so ids cannot be probed.</summary>
    SeatNotFound = 3,

    /// <summary>Lost the race twice, including after a reload. Retryable.</summary>
    LostRace = 4,

    /// <summary>The client already holds the maximum at this event.</summary>
    HoldCapReached = 5,

    /// <summary>
    /// Another hold request by this client for this event is in flight, so the cap cannot
    /// be counted safely. Retryable.
    /// </summary>
    ConcurrentRequestInFlight = 6
}
