namespace Encore.Modules.Orders.Models;

/// <summary>
/// One seat on an <see cref="Order"/>, captured at the price it sold for.
/// </summary>
/// <remarks>
/// <para>
/// <b>The price is copied on purpose, and it is the one field here that must
/// not track its source.</b> Catalog owns what an event costs today; this line
/// owns what the customer agreed to pay. If an operator raises the price an
/// hour after somebody checked out, nothing about that order may change.
/// </para>
/// <para>
/// Nothing else is copied. There is no event name or seat description here,
/// because <see cref="Order.EventId"/> and <see cref="SeatId"/> are enough to
/// render an order and a duplicated name is a second copy of a truth that can
/// drift — the same argument <c>SeatActionResponse</c> makes. Price is different
/// precisely because drifting is what it must not do.
/// </para>
/// <para>
/// One line per seat, and no quantity: a seat is a thing you can buy exactly
/// one of.
/// </para>
/// </remarks>
public sealed class OrderLine
{
    /// <summary>Identity, assigned when the line is created.</summary>
    public Guid Id { get; set; }

    /// <summary>The order this line belongs to.</summary>
    public Guid OrderId { get; set; }

    /// <summary>The seat being bought. Inventory owns whether it is really this client's.</summary>
    public Guid SeatId { get; set; }

    /// <summary>What this seat cost, as of checkout.</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>ISO 4217 code for <see cref="UnitPrice"/>.</summary>
    public string Currency { get; set; } = string.Empty;
}
