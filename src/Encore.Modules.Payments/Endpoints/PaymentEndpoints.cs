using Encore.Modules.Payments.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Payments.Endpoints;

/// <summary>
/// Minimal API endpoints for reading back what happened to a payment.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only, and there is no <c>POST /payments</c>.</b> The stub that stood
/// here proposed one; it is gone on purpose. A client that can charge itself
/// directly has walked around the order flow entirely — it could authorise money
/// against an order it does not own, or against no order at all, and this module
/// would have no principled way to refuse because it does not know what a checkout
/// is. Orders drives payment because Orders is the thing that knows what is owed.
/// That is 022's "confirm and cancel are actions, not a status a client may PATCH"
/// pointed at the other end of the same flow. See <c>DECISIONS.md</c> 033.
/// </para>
/// <para>
/// The cost is real and worth stating: this module has no HTTP path that exercises
/// its write side, so its integration tests carry that weight rather than a
/// request in a scratch file.
/// </para>
/// <para>
/// Every route carries <c>X-Client-Id</c> and is scoped to it. Catalog's routes
/// carry no identity because a catalogue is public; a payment is the opposite of
/// public.
/// </para>
/// </remarks>
public static class PaymentEndpoints
{
    /// <summary>Maps the /payments route group.</summary>
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
    /// Every attempt against one order, newest first.
    /// </summary>
    /// <remarks>
    /// A list rather than a single payment, because an order can accumulate
    /// attempts: a decline is followed by a fresh attempt with a fresh key, and the
    /// history of what was tried is the thing worth reading. <c>orderId</c> is
    /// required — an unfiltered list of a client's payments is a different endpoint
    /// with different paging questions, and nothing needs it yet.
    /// </remarks>
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
