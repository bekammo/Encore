using Encore.Modules.Orders.Models;

namespace Encore.Modules.Orders.Endpoints;

/// <summary>An order as a client sees it.</summary>
/// <remarks>
/// <para>
/// <b><paramref name="Status"/> is the stored status, never a derived one.</b>
/// This response does not decide that a <c>pending</c> order has expired because
/// <paramref name="HoldsExpireAt"/> has passed — deriving it here would be this
/// module judging a rule Inventory owns, and it would be wrong in the other
/// direction too, since a seat released early makes the field optimistic. See
/// <c>DECISIONS.md</c> 021.
/// </para>
/// <para>
/// <paramref name="HoldsExpireAt"/> is returned anyway so a client can render a
/// countdown. The countdown is a hint; the status is the fact.
/// </para>
/// </remarks>
/// <param name="Id">Identity, assigned when checkout started.</param>
/// <param name="EventId">The event being bought into.</param>
/// <param name="Status">Where the order has got to, as a lowercase string.</param>
/// <param name="PlacedAt">When checkout started, in UTC.</param>
/// <param name="HoldsExpireAt">
/// When the first of this order's holds lapses, in UTC. Null once the order has
/// ended.
/// </param>
/// <param name="ClosedAt">When the order reached a terminal status, if it has.</param>
/// <param name="Total">Sum of the lines, as snapshotted at checkout.</param>
/// <param name="Currency">ISO 4217 code for <paramref name="Total"/>.</param>
/// <param name="Lines">One entry per seat.</param>
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
    /// <summary>Renders an order and its lines.</summary>
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

    /// <summary>
    /// <c>AwaitingCapture</c> to <c>awaiting_capture</c>. The four original
    /// statuses are single words and come through this unchanged, so nothing a
    /// client already matches on moves.
    /// </summary>
    private static string SnakeCase(string name) =>
        string.Concat(name.Select((character, index) =>
            char.IsUpper(character) && index > 0
                ? "_" + char.ToLowerInvariant(character)
                : char.ToLowerInvariant(character).ToString()));
}

/// <summary>One seat on an order, at the price it was bought for.</summary>
/// <param name="SeatId">The seat.</param>
/// <param name="UnitPrice">What it cost, as of checkout.</param>
/// <param name="Currency">ISO 4217 code for <paramref name="UnitPrice"/>.</param>
public sealed record OrderLineResponse(Guid SeatId, decimal UnitPrice, string Currency);
