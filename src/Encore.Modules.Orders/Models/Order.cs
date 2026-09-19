namespace Encore.Modules.Orders.Models;

/// <summary>
/// A customer's purchase: who bought what, when, and for how much. Persistence
/// POCO, not an aggregate — state transitions that matter live in Inventory
/// and Payments.
/// </summary>
/// <remarks>
/// <para>
/// The distinction is real rather than a disclaimer. A seat can be sold once
/// and the rule is enforced inside <c>Seat</c>; an order is a record of what
/// Inventory already decided, and every rule about it spans this row and
/// Inventory's, so none of them could live here even if this type wanted them.
/// Public setters are the honest shape for that.
/// </para>
/// <para>
/// <b><see cref="HoldsExpireAt"/> is copied, never computed.</b> Inventory owns
/// the hold window; this module records the answer it was given and re-asks
/// rather than re-deciding. Orders does not know the number five, and a confirm
/// is never refused here because this field has passed — see
/// <c>DECISIONS.md</c> 021.
/// </para>
/// </remarks>
public sealed class Order
{
    /// <summary>Identity, assigned when checkout starts.</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Who is buying. The same claimed identity Inventory holds seats for —
    /// <c>X-Client-Id</c>, not an authenticated user, because there is no
    /// Identity module yet (<c>DECISIONS.md</c> 014).
    /// </summary>
    public Guid ClientId { get; set; }

    /// <summary>
    /// The event being bought into. One event per order, which is what makes one
    /// currency per order structural rather than assumed.
    /// </summary>
    public Guid EventId { get; set; }

    /// <summary>Where this order has got to.</summary>
    public OrderStatus Status { get; set; }

    /// <summary>When checkout started. Always UTC.</summary>
    public DateTime PlacedAt { get; set; }

    /// <summary>
    /// The earliest instant at which any of this order's holds lapses, as
    /// Inventory reported it at checkout. Null once the order is no longer
    /// <see cref="OrderStatus.Pending"/>.
    /// </summary>
    /// <remarks>
    /// The earliest rather than the latest, because an order needs every one of
    /// its seats: the first hold to lapse is the moment the order stops being
    /// completable. It is advisory — a seat released early makes it optimistic,
    /// and only Inventory can say for certain.
    /// </remarks>
    public DateTime? HoldsExpireAt { get; set; }

    /// <summary>When the order reached a terminal status, if it has.</summary>
    public DateTime? ClosedAt { get; set; }

    /// <summary>Sum of the lines, snapshotted at checkout.</summary>
    public decimal Total { get; set; }

    /// <summary>ISO 4217 code for <see cref="Total"/>, copied from the event.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>
    /// Concurrency token, mapped to the Postgres <c>xmin</c> system column as
    /// <c>Seat</c>'s is.
    /// </summary>
    /// <remarks>
    /// Orders is a module over uncontended tables, with one exception: confirm
    /// and cancel arriving together — an impatient double-click — really do race
    /// this row, and without a token the loser can write <c>Cancelled</c> over an
    /// order whose seats are already <c>Sold</c>. Inventory protects the seats
    /// either way, so this guards Orders' own record rather than the invariant.
    /// </remarks>
    public uint RowVersion { get; set; }

    /// <summary>One line per seat, in the order they were held.</summary>
    public List<OrderLine> Lines { get; set; } = [];
}
