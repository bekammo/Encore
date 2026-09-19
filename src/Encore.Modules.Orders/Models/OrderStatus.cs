namespace Encore.Modules.Orders.Models;

/// <summary>
/// Where an order has got to. A closed set with two non-terminal members
/// (<see cref="Pending"/> and <see cref="AwaitingCapture"/>); everything else is
/// an ending.
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
    /// that succeeded. The customer is <b>not</b> charged: the authorisation is
    /// released, because taking money for an order that did not complete is the
    /// worse of the two ways to be wrong. That answers the open question this
    /// remark used to carry — see 028.
    /// </remarks>
    Failed = 4,

    /// <summary>
    /// The seats are sold and the funds are held, but the capture has not gone
    /// through. The customer has their tickets; the money has not been taken yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not terminal, and appended rather than inserted.</b> 021 called the set
    /// of endings closed, and this supersedes that half of it — the append-only
    /// log gets a new entry (027) rather than an edit. Appended because
    /// <see cref="Pending"/> is pinned to zero and the partial unique index
    /// filters on the literal <c>0</c>; renumbering anything here would leave that
    /// index guarding the wrong rows.
    /// </para>
    /// <para>
    /// Resolved by the next touch: a retried <c>POST /orders/{id}/confirm</c>
    /// retries the capture and moves to <see cref="Confirmed"/>. There is no
    /// background job, for 021's own reason — a stale row here is untidy, not
    /// incorrect, and a sweep that anything depended on would be load-bearing.
    /// </para>
    /// <para>
    /// Kept apart from <see cref="Failed"/> deliberately. Both want a human
    /// eventually, but an operator looking at this one should retry a capture,
    /// and one looking at <see cref="Failed"/> should apologise — and 021's
    /// argument for splitting <see cref="Expired"/> from <see cref="Cancelled"/>
    /// applies with full force: history cannot be backfilled once the distinction
    /// has been thrown away.
    /// </para>
    /// </remarks>
    AwaitingCapture = 5
}
