namespace Encore.Modules.Orders.Models;

/// <summary>
/// A persistence POCO, not an aggregate: every rule about an order spans this row and
/// Inventory's, so its transitions live in <see cref="CheckoutService"/>.
/// </summary>
public sealed class Order
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }

    public Guid EventId { get; set; }

    public OrderStatus Status { get; set; }

    public DateTime PlacedAt { get; set; }

    /// <summary>
    /// The earliest hold expiry Inventory reported, copied and never computed; null once the seats
    /// sold or the order ended. Advisory only: Orders never refuses a confirm or derives a status
    /// from it (009).
    /// </summary>
    public DateTime? HoldsExpireAt { get; set; }

    public DateTime? ClosedAt { get; set; }

    public DateTime? SoldAt { get; set; }

    public decimal Total { get; set; }

    public string Currency { get; set; } = string.Empty;

    /// <summary><c>xmin</c>: a confirm and a cancel of one order really can race (009).</summary>
    public uint RowVersion { get; set; }

    public List<OrderLine> Lines { get; set; } = [];
}
