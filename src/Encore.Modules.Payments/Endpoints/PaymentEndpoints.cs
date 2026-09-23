using Encore.Modules.Payments.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Payments.Endpoints;

/// <summary>
/// Read-only endpoints for a client's payments. There is no <c>POST /payments</c>: a client
/// that could charge itself would bypass the order flow. Scoped to <c>X-Client-Id</c>.
/// </summary>
public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/payments")
            .AddEndpointFilter<ClientIdEndpointFilter>();

        group.MapGet("/{paymentId:guid}", GetPaymentAsync);
        group.MapGet("/", ListForOrderAsync);

        return endpoints;
    }

    /// <summary>Reads one attempt back.</summary>
    private static async Task<IResult> GetPaymentAsync(
        Guid paymentId,
        HttpContext http,
        PaymentsDbContext payments,
        CancellationToken cancellationToken)
    {
        var clientId = ClientIdEndpointFilter.ClientId(http);

        var payment = await payments.Payments
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == paymentId && candidate.ClientId == clientId,
                cancellationToken)
            .ConfigureAwait(false);

        return payment is null
            ? PaymentResults.NotFound(http.Request.Path)
            : TypedResults.Ok(PaymentResponse.From(payment));
    }

    /// <summary>
    /// Every attempt against one order, newest first. <c>orderId</c> is required.
    /// </summary>
    private static async Task<IResult> ListForOrderAsync(
        Guid orderId,
        HttpContext http,
        PaymentsDbContext payments,
        CancellationToken cancellationToken)
    {
        var clientId = ClientIdEndpointFilter.ClientId(http);

        var found = await payments.Payments
            .AsNoTracking()
            .Where(candidate => candidate.OrderId == orderId && candidate.ClientId == clientId)
            .OrderByDescending(candidate => candidate.AttemptedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(found.Select(PaymentResponse.From).ToArray());
    }
}
