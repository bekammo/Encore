namespace Encore.Modules.Orders.Models;

/// <summary>
/// Where an order has got to. A closed set with one non-terminal member
/// (<see cref="Pending"/>); everything else is an ending.
/// </summary>
/// <remarks>
/// The transitions and the reasoning are <c>DECISIONS.md</c> 021. The two worth
/// knowing here: <see cref="Expired"/> is kept apart from <see cref="Cancelled"/>
/// because "your hold ran out" and "you changed your mind" are different things
/// to tell a customer and order history cannot be backfilled later, and
/// <see cref="Failed"/> is the only status that means a person has to look.
/// </remarks>
public enum OrderStatus
{
    /// <summary>
    /// Seats are held and the customer has not finished. The only status from
    /// which anything else can happen.
    /// </summary>
    /// <remarks>
    /// <b>Pinned to zero.</b> The partial unique index that allows one open
    /// checkout per client per event filters on <c>"Status" = 0</c>, a literal
    /// in SQL that no compiler checks against this enum. Renumbering these
    /// members would leave the index quietly guarding the wrong rows.
    /// </remarks>
    Pending = 0,

    /// <summary>Every seat sold. The customer has their tickets.</summary>
    Confirmed = 1,

    /// <summary>The customer abandoned it, and the seats were handed back.</summary>
    Cancelled = 2,

    /// <summary>
    /// The holds lapsed before checkout completed, and nothing was sold. Reached
    /// only when a confirm was attempted and Inventory said the holds had gone —
    /// never by Orders watching its own clock.
    /// </summary>
    Expired = 3,

    /// <summary>
    /// Checkout broke in a way that leaves the order inconsistent: some seats
    /// sold and others did not, or a refusal that was not expiry. Terminal, and
    /// the only status that needs a human.
    /// </summary>
    /// <remarks>
    /// There is no automatic recovery because there cannot be one — a sold seat
    /// is terminal (<c>DECISIONS.md</c> 007), so nothing can un-sell the part
    /// that succeeded. Whether the customer is charged for a partial order or
    /// refunded is a Payments question that does not exist yet.
    /// </remarks>
    Failed = 4
}
