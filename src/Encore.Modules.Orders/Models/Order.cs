namespace Encore.Modules.Orders.Models;

/// <summary>
/// A customer's purchase. A persistence POCO, not an aggregate: every rule about an order
/// spans this row and Inventory's, so the transitions that matter live elsewhere.
/// </summary>
public sealed class Order
{
    public Guid Id { get; set; }

    /// <summary>
    /// Who is buying: the claimed <c>X-Client-Id</c>, not an authenticated user.
    /// </summary>
    public Guid ClientId { get; set; }

    /// <summary>
    /// The event being bought into. One event per order means one currency per order.
    /// </summary>
    public Guid EventId { get; set; }

    public OrderStatus Status { get; set; }

    /// <summary>When checkout started. Always UTC.</summary>
    public DateTime PlacedAt { get; set; }

    /// <summary>
    /// The earliest hold expiry Inventory reported at checkout. Copied, never computed,
    /// and advisory only: Orders never refuses a confirm because it has passed.
    /// </summary>
    public DateTime? HoldsExpireAt { get; set; }

    /// <summary>When the order reached a terminal status, if it has.</summary>
    public DateTime? ClosedAt { get; set; }

    /// <summary>
    /// When every seat sold, so the capture became owed. Recorded before the capture is asked
    /// for, which is what lets the capture sweep find an order a confirm left unfinished.
    /// </summary>
    public DateTime? SoldAt { get; set; }

    /// <summary>Sum of the lines, snapshotted at checkout.</summary>
    public decimal Total { get; set; }

    /// <summary>ISO 4217 code for <see cref="Total"/>, copied from the event.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>
    /// Concurrency token (<c>xmin</c>). A confirm and a cancel of one order really can race.
    /// </summary>
    public uint RowVersion { get; set; }

    /// <summary>One line per seat, in the order they were held.</summary>
    public List<OrderLine> Lines { get; set; } = [];
}
