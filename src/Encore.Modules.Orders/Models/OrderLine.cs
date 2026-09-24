namespace Encore.Modules.Orders.Models;

public sealed class OrderLine
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }

    public Guid SeatId { get; set; }

    public decimal UnitPrice { get; set; }

    public string Currency { get; set; } = string.Empty;
}
