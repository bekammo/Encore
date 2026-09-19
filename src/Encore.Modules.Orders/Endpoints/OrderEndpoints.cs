using Encore.Modules.Orders.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Orders.Endpoints;

/// <summary>
/// Minimal API endpoints for placing, reading and ending orders.
/// </summary>
/// <remarks>
/// <para>
/// An order here is a record of what was bought. The hard part — does the seat
/// exist, is it still free, whose is it — belongs to Inventory, and this module
/// asks rather than deciding.
/// </para>
/// <para>
/// <b>Confirm and cancel are actions, not a status field a client may PATCH.</b>
/// 014 made the same call for seats and the reason carries: the set of endings
/// is closed and the rules for reaching each one are not the client's to apply.
/// A writable status would invite a client to declare an order <c>confirmed</c>
/// without a single seat having been sold.
/// </para>
/// <para>
/// Every handler is a call plus a mapping, with no branching:
/// <see cref="OrderResults"/> owns the status decisions so they can be tested
/// without a host.
/// </para>
/// </remarks>
public static class OrderEndpoints
{
    /// <summary>Maps the /orders route group.</summary>
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Every route here acts on behalf of a client, so the filter goes on the
        // group: an endpoint added later cannot forget it.
        var orders = endpoints.MapGroup("/orders")
            .AddEndpointFilter<ClientIdEndpointFilter>();

        // An empty pattern rather than "/", so the route is exactly the group
        // prefix. "/" would append a trailing empty segment and leave whether
        // POST /orders matches up to route normalisation rather than to this file.
        orders.MapPost("", CheckoutAsync)
            .WithName("Checkout")
            .WithSummary("Holds the seats and opens an order for them.");

        orders.MapGet("/{orderId:guid}", GetAsync)
            .WithName("GetOrder")
            .WithSummary("Reads one of the calling client's orders.");

        orders.MapPost("/{orderId:guid}/confirm", ConfirmAsync)
            .WithName("ConfirmOrder")
            .WithSummary("Converts the order's holds into sales.");

        orders.MapPost("/{orderId:guid}/cancel", CancelAsync)
            .WithName("CancelOrder")
            .WithSummary("Ends the order because the customer said so.");

        return endpoints;
    }

    private static async Task<IResult> CheckoutAsync(
        CheckoutRequest request,
        CheckoutService checkout,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var result = await checkout
            .CheckoutAsync(
                ClientIdEndpointFilter.ClientId(context),
                request.EventId,
                request.SeatIds ?? [],
                cancellationToken)
            .ConfigureAwait(false);

        return OrderResults.ForCheckout(result, context.Request.Path);
    }

    /// <remarks>
    /// Reads the stored status and derives nothing. An order whose
    /// <c>HoldsExpireAt</c> has passed still reads <c>pending</c> here, because
    /// only Inventory can say whether those holds are really gone — see 021.
    /// </remarks>
    private static async Task<IResult> GetAsync(
        Guid orderId,
        OrdersDbContext orders,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var clientId = ClientIdEndpointFilter.ClientId(context);

        var order = await orders.Orders
            .AsNoTracking()
            .Include(order => order.Lines)
            .SingleOrDefaultAsync(
                order => order.Id == orderId && order.ClientId == clientId,
                cancellationToken)
            .ConfigureAwait(false);

        return order is null
            ? OrderResults.NotFound(context.Request.Path)
            : OrderResults.ForRead(order);
    }

    private static async Task<IResult> ConfirmAsync(
        Guid orderId,
        CheckoutService checkout,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var result = await checkout
            .ConfirmAsync(ClientIdEndpointFilter.ClientId(context), orderId, cancellationToken)
            .ConfigureAwait(false);

        return OrderResults.ForConfirm(result, context.Request.Path);
    }

    private static async Task<IResult> CancelAsync(
        Guid orderId,
        CheckoutService checkout,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var result = await checkout
            .CancelAsync(ClientIdEndpointFilter.ClientId(context), orderId, cancellationToken)
            .ConfigureAwait(false);

        return OrderResults.ForCancel(result, context.Request.Path);
    }
}
