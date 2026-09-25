using Encore.Modules.Orders.Data;
using Encore.Modules.Shared.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Orders.Endpoints;

/// <summary>Confirm and cancel are actions, not a status a client may write (008).</summary>
public static class OrderEndpoints
{
    public const string CheckoutRateLimitPolicy = "orders-checkout";

    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var orders = endpoints.MapGroup("/orders")
            .AddEndpointFilter<ClientIdEndpointFilter>();

        // Empty pattern, so the route is exactly /orders.
        // Checkout takes holds, the writes a bot would hammer (030).
        orders.MapPost("", CheckoutAsync)
            .RequireRateLimiting(CheckoutRateLimitPolicy);

        orders.MapGet("/{orderId:guid}", GetAsync);
        orders.MapPost("/{orderId:guid}/confirm", ConfirmAsync);
        orders.MapPost("/{orderId:guid}/cancel", CancelAsync);

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
