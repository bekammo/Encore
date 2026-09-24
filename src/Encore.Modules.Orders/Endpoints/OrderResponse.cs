using Encore.Modules.Orders.Models;

namespace Encore.Modules.Orders.Endpoints;

public sealed record OrderResponse(
    Guid Id,
    Guid EventId,
    string Status,
    DateTime PlacedAt,
    DateTime? HoldsExpireAt,
    DateTime? ClosedAt,
    decimal Total,
    string Currency,
    IReadOnlyList<OrderLineResponse> Lines)
{
    public static OrderResponse From(Order order) =>
        new(
            order.Id,
            order.EventId,
            SnakeCase(order.Status.ToString()),
            order.PlacedAt,
            order.HoldsExpireAt,
            order.ClosedAt,
            order.Total,
            order.Currency,
            [.. order.Lines.Select(line => new OrderLineResponse(line.SeatId, line.UnitPrice, line.Currency))]);

    private static string SnakeCase(string name) =>
        string.Concat(name.Select((character, index) =>
            char.IsUpper(character) && index > 0
                ? "_" + char.ToLowerInvariant(character)
                : char.ToLowerInvariant(character).ToString()));
}

public sealed record OrderLineResponse(Guid SeatId, decimal UnitPrice, string Currency);
