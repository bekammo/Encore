using Encore.Modules.Payments.Data;
using Encore.Modules.Shared.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Payments.Endpoints;

/// <summary>No <c>POST /payments</c>: a client that could charge itself would bypass the order flow.</summary>
public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var payments = endpoints
            .MapGroup("/payments")
            .AddEndpointFilter<ClientIdEndpointFilter>();

        payments.MapGet("/{paymentId:guid}", GetPaymentAsync);
        payments.MapGet("/", ListForOrderAsync);

        return endpoints;
    }

    private static async Task<IResult> GetPaymentAsync(
        Guid paymentId,
        HttpContext context,
        PaymentsDbContext payments,
        CancellationToken cancellationToken)
    {
        var clientId = ClientIdEndpointFilter.ClientId(context);

        var payment = await payments.Payments
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == paymentId && candidate.ClientId == clientId,
                cancellationToken)
            .ConfigureAwait(false);

        return payment is null
            ? NotFound(context.Request.Path)
            : TypedResults.Ok(PaymentResponse.From(payment));
    }

    private static async Task<IResult> ListForOrderAsync(
        Guid orderId,
        HttpContext context,
        PaymentsDbContext payments,
        CancellationToken cancellationToken)
    {
        var clientId = ClientIdEndpointFilter.ClientId(context);

        var found = await payments.Payments
            .AsNoTracking()
            .Where(candidate => candidate.OrderId == orderId && candidate.ClientId == clientId)
            .OrderByDescending(candidate => candidate.AttemptedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(found.Select(PaymentResponse.From).ToArray());
    }

    // Not there, or not this client's: the same answer, so ids cannot be probed.
    private static IResult NotFound(PathString path) =>
        TypedResults.Problem(
            detail: "No such payment.",
            statusCode: StatusCodes.Status404NotFound,
            title: "Payment not found",
            instance: path,
            extensions: new Dictionary<string, object?> { ["reason"] = "payment_not_found" });
}
