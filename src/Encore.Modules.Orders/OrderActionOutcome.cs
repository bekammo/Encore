namespace Encore.Modules.Orders;

/// <summary>
/// Every way confirming or cancelling an order can end. <see cref="Completed"/> means the
/// operation ran; the ending itself is on the order's <see cref="Models.OrderStatus"/>.
/// </summary>
public enum OrderActionOutcome
{
    /// <summary>The operation ran. Read the order's status for what it decided.</summary>
    Completed = 0,

    /// <summary>
    /// No such order for this client. Someone else's order gets the same answer.
    /// </summary>
    OrderNotFound = 1,

    /// <summary>
    /// The order has already ended. Repeating the same action is <see cref="Completed"/> instead.
    /// </summary>
    NotPending = 2,

    /// <summary>
    /// Another writer reached the order row first. Retryable.
    /// </summary>
    LostRace = 3,

    /// <summary>
    /// The card was declined. The order stays pending with its holds, so the customer can retry.
    /// </summary>
    PaymentDeclined = 4,

    /// <summary>
    /// The gateway did not answer. The order stays pending; a retry reuses the same payment key.
    /// </summary>
    PaymentTimedOut = 5
}
