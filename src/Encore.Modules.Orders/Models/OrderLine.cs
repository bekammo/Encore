namespace Encore.Modules.Orders.Models;

/// <summary>
/// One seat on an <see cref="Order"/>, captured at the price it sold for.
/// </summary>
/// <remarks>
/// The price is copied so a later price change never touches an existing order. Nothing
/// else is copied. One line per seat, no quantity.
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
