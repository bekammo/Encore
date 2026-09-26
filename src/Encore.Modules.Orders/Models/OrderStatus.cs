namespace Encore.Modules.Orders.Models;

public enum OrderStatus
{
    /// <summary>
    /// Pinned to zero: the one-open-checkout index filters on the SQL literal <c>"Status" = 0</c>.
    /// </summary>
    Pending = 0,

    Confirmed = 1,

    Cancelled = 2,

    Expired = 3,

    Failed = 4,

    /// <summary>
    /// Not an ending, so ClosedAt stays unset. Appended, never renumbered: the column stores
    /// the number.
    /// </summary>
    AwaitingCapture = 5,

    /// <summary>
    /// Every seat is sold and nothing is held: the gateway refused the capture (034). Not an
    /// ending, so ClosedAt stays unset; the customer's next confirm authorises again.
    /// </summary>
    PaymentDue = 6
}
