using Encore.Modules.Payments.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Encore.Modules.Payments.Endpoints;

/// <summary>
/// The write side of Payments, over HTTP, for one caller: Orders. DECISIONS 061.
/// </summary>
/// <remarks>
/// <para>
/// This exists so that Payments can be extracted into its own process. In-process
/// Orders reaches this module through <see cref="IOrderPayments"/> resolved from the
/// container; out of process it reaches the same three operations through these
/// three routes, and the adapter on the far side maps one to the other exactly.
/// </para>
/// <para>
/// <b>DECISIONS 033 still holds.</b> It refused a customer-facing write surface
/// because a client that can charge itself has walked around the order flow. Nothing
/// here is customer-facing: the group is mounted by <see cref="MapPaymentServiceEndpoints"/>, which
/// <c>MapPaymentsModule</c> does not call, it carries no <c>X-Client-Id</c>, and
/// <see cref="ServiceTokenEndpointFilter"/> refuses anything that cannot present the
/// service token. A caller holding a client id and nothing else gets 401 here.
/// </para>
/// <para>
/// <b>Why <c>/internal</c> and not versioned resource routes.</b> The seam is an RPC
/// seam and the URLs say so. <c>IOrderPayments</c> is keyed by order, not by payment,
/// so <c>POST /internal/payments/capture</c> with an order in the body is the honest
/// spelling; <c>POST /payments/{id}/capture</c> would mean Orders storing a payment
/// id it has never needed, to address a row the one-live-attempt index already picks
/// out. A path prefix is also the thing an ingress can refuse in one rule.
/// </para>
/// </remarks>
public static class PaymentServiceEndpoints
{
    /// <summary>
    /// Maps the service API. Called by the Payments host, and by the monolith only
    /// while the extraction is in progress.
    /// </summary>
    /// <param name="endpoints">Where to map.</param>
    /// <param name="serviceToken">
    /// The shared secret every caller must present. Empty is a configuration error
    /// rather than an open door — see <c>PaymentsModule.MapPaymentsServiceApi</c>.
    /// </param>
    public static IEndpointRouteBuilder MapPaymentServiceEndpoints(
        this IEndpointRouteBuilder endpoints,
        string serviceToken)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceToken);

        // The prefix is spelled out rather than taken from PaymentsServiceApi.Prefix,
        // and that is deliberate. DECISIONS 059's reader resolves a group by reading
        // the literal out of the source, so a constant here makes these three routes
        // unreadable — they were being compared as "/authorize" until its fourth test
        // said so. PaymentsServiceApiTests pins the two spellings together instead.
        var group = endpoints
            .MapGroup("/internal/payments")
            .AddEndpointFilter(new ServiceTokenEndpointFilter(serviceToken));

        group.MapPost("/authorize", AuthorizeAsync);
        group.MapPost("/capture", CaptureAsync);
        group.MapPost("/void", VoidAsync);

        return endpoints;
    }

    /// <summary>Opens or reuses this order's authorisation.</summary>
    private static async Task<IResult> AuthorizeAsync(
        AuthorizeAttemptRequest request,
        HttpContext http,
        IOrderPayments payments,
        CancellationToken cancellationToken)
    {
        var response = await payments
            .AuthorizeAsync(
                new AuthorizePaymentRequest(
                    request.OrderId, request.ClientId, request.Amount, request.Currency),
                cancellationToken)
            .ConfigureAwait(false);

        return response.Status switch
        {
            // Both are answers rather than refusals: a retry after a dropped response
            // must get back what it already has, which is the same reason re-holding
            // a seat you already hold is a no-op success (007).
            AuthorizePaymentStatus.Authorized =>
                Ok("authorized", response.PaymentId),
            AuthorizePaymentStatus.AlreadyCaptured =>
                Ok("already_captured", response.PaymentId),

            AuthorizePaymentStatus.Declined => Refused(
                http, StatusCodes.Status402PaymentRequired,
                "Payment declined", "declined", response.PaymentId),

            // 504 and not 500: the gateway is a thing upstream of this service that
            // did not answer in time, which is exactly what the code means. The
            // attempt is recorded and the reconciler will settle it.
            AuthorizePaymentStatus.TimedOut => Refused(
                http, StatusCodes.Status504GatewayTimeout,
                "The gateway did not answer", "timed_out", response.PaymentId),

            AuthorizePaymentStatus.ConcurrentAttemptInFlight => Refused(
                http, StatusCodes.Status409Conflict,
                "Another attempt for this order is in flight",
                "concurrent_attempt_in_flight", response.PaymentId),

            _ => throw new ArgumentOutOfRangeException(
                nameof(request), response.Status, "Unmapped authorize status.")
        };
    }

    /// <summary>Takes the funds this order's attempt is holding.</summary>
    private static async Task<IResult> CaptureAsync(
        OrderAttemptRequest request,
        HttpContext http,
        IOrderPayments payments,
        CancellationToken cancellationToken)
    {
        var response = await payments
            .CaptureAsync(
                new CapturePaymentRequest(request.OrderId, request.ClientId),
                cancellationToken)
            .ConfigureAwait(false);

        return response.Status switch
        {
            CapturePaymentStatus.Captured => Ok("captured", response.PaymentId),

            CapturePaymentStatus.TimedOut => Refused(
                http, StatusCodes.Status504GatewayTimeout,
                "The gateway did not answer", "timed_out", response.PaymentId),

            CapturePaymentStatus.NoAuthorization => Refused(
                http, StatusCodes.Status409Conflict,
                "There is nothing held for this order", "no_authorization", response.PaymentId),

            _ => throw new ArgumentOutOfRangeException(
                nameof(request), response.Status, "Unmapped capture status.")
        };
    }

    /// <summary>Releases what this order's attempt is holding.</summary>
    private static async Task<IResult> VoidAsync(
        OrderAttemptRequest request,
        HttpContext http,
        IOrderPayments payments,
        CancellationToken cancellationToken)
    {
        var response = await payments
            .VoidAsync(
                new VoidPaymentRequest(request.OrderId, request.ClientId),
                cancellationToken)
            .ConfigureAwait(false);

        return response.Status switch
        {
            VoidPaymentStatus.Voided => Ok("voided", response.PaymentId),

            VoidPaymentStatus.TimedOut => Refused(
                http, StatusCodes.Status504GatewayTimeout,
                "The gateway did not answer", "timed_out", response.PaymentId),

            // The money is already gone and this module will not pretend otherwise.
            // Orders reads this as a lost race, which is what it is.
            VoidPaymentStatus.AlreadyCaptured => Refused(
                http, StatusCodes.Status409Conflict,
                "That attempt has already been captured", "already_captured", response.PaymentId),

            VoidPaymentStatus.NoAuthorization => Refused(
                http, StatusCodes.Status409Conflict,
                "There is nothing held for this order", "no_authorization", response.PaymentId),

            _ => throw new ArgumentOutOfRangeException(
                nameof(request), response.Status, "Unmapped void status.")
        };
    }

    private static IResult Ok(string outcome, Guid? paymentId) =>
        TypedResults.Ok(new PaymentOutcomeResponse(
            outcome,
            paymentId ?? throw new InvalidOperationException(
                $"A '{outcome}' outcome must carry the attempt it is about.")));

    /// <summary>
    /// A refusal in the one shape 049 gave this host, carrying the two things the
    /// caller actually branches on.
    /// </summary>
    /// <remarks>
    /// <c>paymentId</c> is an extension rather than part of the detail string because
    /// the adapter reads it back: <c>TimedOut</c> names the attempt the reconciler
    /// will settle, and losing it would leave Orders unable to say which one.
    /// <c>ConcurrentAttemptInFlight</c> and <c>NoAuthorization</c> carry none, and the
    /// extension is simply absent rather than null.
    /// </remarks>
    private static IResult Refused(
        HttpContext http,
        int statusCode,
        string title,
        string reason,
        Guid? paymentId)
    {
        var extensions = new Dictionary<string, object?> { ["reason"] = reason };

        if (paymentId is { } id)
        {
            extensions["paymentId"] = id;
        }

        return TypedResults.Problem(
            detail: title + ".",
            statusCode: statusCode,
            title: title,
            instance: http.Request.Path,
            extensions: extensions);
    }
}
