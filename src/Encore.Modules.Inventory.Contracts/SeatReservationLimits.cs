namespace Encore.Modules.Inventory.Contracts;

/// <summary>
/// The published hold cap (005). Fields rather than <c>const</c>, so a consumer compiled
/// against an old value does not keep using it.
/// </summary>
public static class SeatReservationLimits
{
    public static readonly int MaxHoldsPerClientPerEvent = 4;
}
