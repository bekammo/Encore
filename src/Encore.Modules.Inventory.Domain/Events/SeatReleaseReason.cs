namespace Encore.Modules.Inventory.Domain.Events;

/// <summary>
/// Why a seat returned to the pool. Recorded from the start because event history
/// cannot be backfilled.
/// </summary>
public enum SeatReleaseReason
{
    /// <summary>The holder gave the seat up.</summary>
    Cancelled = 0,

    /// <summary>The hold lapsed.</summary>
    Expired = 1
}
