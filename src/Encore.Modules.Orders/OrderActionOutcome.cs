namespace Encore.Modules.Orders;

/// <summary>
/// Every way confirming or cancelling an order can end.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Completed"/> does not mean the customer got what they wanted.</b>
/// It means the operation ran and the order reached an ending; which ending is
/// on the order's own <see cref="Models.OrderStatus"/>. That split is deliberate
/// and it is 021's rule about where truth lives: the status is the fact, and
/// nothing above it gets a second opinion. A confirm whose holds had lapsed
/// comes back <see cref="Completed"/> with an order reading
/// <see cref="Models.OrderStatus.Expired"/>.
/// </para>
/// <para>
/// The alternative — an outcome per ending — would mean this enum and
/// <see cref="Models.OrderStatus"/> both encoding the same four endings, and
/// two encodings of one fact is one more than can stay correct.
/// </para>
/// </remarks>
public enum OrderActionOutcome
{
    /// <summary>The operation ran. Read the order's status for what it decided.</summary>
    Completed = 0,

    /// <summary>
    /// No such order for this client. Deliberately the same answer for an order
    /// that belongs to somebody else, so order ids cannot be discovered by
    /// asking about them — the call
    /// <see cref="Encore.Modules.Inventory.Contracts.HoldSeatStatus.SeatNotFound"/>
    /// already makes for seats.
    /// </summary>
    OrderNotFound = 1,

    /// <summary>
    /// The order has already ended, and endings are terminal. Re-confirming a
    /// <c>Confirmed</c> order and re-cancelling a <c>Cancelled</c> one are
    /// <see cref="Completed"/> rather than this, so a retried request after a
    /// dropped response is safe.
    /// </summary>
    NotPending = 2,

    /// <summary>
    /// Another writer reached the order row first — the impatient double-click
    /// <c>OrderConfiguration</c> carries <c>xmin</c> for. Worth retrying.
    /// </summary>
    LostRace = 3,

    /// <summary>
    /// The gateway refused the card. The order is untouched and still
    /// <see cref="Models.OrderStatus.Pending"/>, so the customer can try again
    /// with a different one while the holds are still live.
    /// </summary>
    /// <remarks>
    /// Not <see cref="Completed"/>, because that member promises the order
    /// reached an ending and this one deliberately did not. A decline is the most
    /// ordinary payment failure there is, and ending an order on it would throw
    /// away four live holds over a typo'd expiry date.
    /// </remarks>
    PaymentDeclined = 4,

    /// <summary>
    /// The gateway did not answer, so whether funds are held is unknown. The order
    /// is untouched and still <see cref="Models.OrderStatus.Pending"/>; the
    /// attempt is recorded in Payments and a retry asks the same question under
    /// the same key rather than a second one.
    /// </summary>
    PaymentTimedOut = 5
}
