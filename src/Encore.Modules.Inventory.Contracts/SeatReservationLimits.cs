namespace Encore.Modules.Inventory.Contracts;

/// <summary>
/// Limits a caller can read before acting instead of learning them by being refused.
/// Fields rather than <c>const</c>, so a consumer compiled against an old value does not
/// keep using it.
/// </summary>
public static class SeatReservationLimits
{
    /// <summary>
    /// Seats one client may hold at one event at once. Inventory still judges each request,
    /// so reading this does not guarantee a hold will succeed.
    /// </summary>
    public static readonly int MaxHoldsPerClientPerEvent = 4;
}
