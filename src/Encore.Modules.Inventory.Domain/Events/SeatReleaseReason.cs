namespace Encore.Modules.Inventory.Domain.Events;

/// <summary>
/// Why a seat returned to the available pool.
/// </summary>
/// <remarks>
/// Captured from the start rather than added when something needs it, because an
/// append-only log is the one place YAGNI has an asymmetric cost: a consumer can
/// be added later, history cannot. Without this, "what share of holds time out
/// versus get abandoned" — the number that says whether the hold window is the
/// right length — would be unanswerable for every release written before the
/// field existed.
/// </remarks>
public enum SeatReleaseReason
{
    /// <summary>The holding client gave the seat up deliberately.</summary>
    Cancelled = 0,

    /// <summary>The hold lapsed. Raised by lazy reclaim or by the sweep.</summary>
    Expired = 1
}
