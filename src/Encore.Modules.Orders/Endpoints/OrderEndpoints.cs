using Encore.Modules.Orders.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Orders.Endpoints;

/// <summary>
/// Endpoints for placing, reading and ending orders. Confirm and cancel are actions, not a
/// status a client may write.
/// </summary>
public static class OrderEndpoints
{
    /// <summary>Maps the /orders route group.</summary>
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // On the group, so a route added later cannot forget the client filter.
        var orders = endpoints.MapGroup("/orders")
            .AddEndpointFilter<ClientIdEndpointFilter>();

        // Empty pattern, so the route is exactly /orders.
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

    /// <remarks>Returns the stored status; only Inventory can say whether holds have lapsed.</remarks>
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
