namespace Encore.Modules.Orders.Models;

/// <summary>
/// Where an order has got to. <see cref="Pending"/> and <see cref="AwaitingCapture"/> are
/// non-terminal; everything else is an ending.
/// </summary>
public enum OrderStatus
{
    /// <summary>
    /// Seats are held and the customer has not finished. Pinned to zero: the
    /// one-open-checkout index filters on the SQL literal <c>"Status" = 0</c>.
    /// </summary>
    Pending = 0,

    /// <summary>Every seat sold and the money taken.</summary>
    Confirmed = 1,

    /// <summary>The customer cancelled and the seats were handed back.</summary>
    Cancelled = 2,

    /// <summary>The holds lapsed before checkout completed, as reported by Inventory.</summary>
    Expired = 3,

    /// <summary>Checkout could not complete for a reason other than expiry. Needs a person to look.</summary>
    Failed = 4,

    /// <summary>
    /// Seats sold and funds held, but the capture has not gone through. The next confirm
    /// retries it. Appended, never inserted, so existing values keep their meaning.
    /// </summary>
    AwaitingCapture = 5
}
