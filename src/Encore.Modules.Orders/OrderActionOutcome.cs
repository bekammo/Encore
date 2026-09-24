namespace Encore.Modules.Orders;

public enum OrderActionOutcome
{
    /// <summary>The operation ran. Read the order's status for what it decided.</summary>
    Completed = 0,

    OrderNotFound = 1,

    /// <summary>
    /// Never for a repeat of the action that ended the order: that is <see cref="Completed"/>.
    /// </summary>
    NotPending = 2,

    LostRace = 3,

    PaymentDeclined = 4,

    PaymentTimedOut = 5
}
