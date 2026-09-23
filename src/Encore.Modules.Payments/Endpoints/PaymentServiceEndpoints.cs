using Encore.Modules.Payments.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Outcomes = Encore.Modules.Payments.Contracts.PaymentsServiceApi.Outcomes;

namespace Encore.Modules.Payments.Endpoints;

/// <summary>
/// The write side of Payments over HTTP, for one caller: Orders, when Payments runs as its
/// own service. Not customer-facing: no <c>X-Client-Id</c>, a service token instead, and an
/// <c>/internal</c> prefix an ingress can refuse. RPC-shaped because the seam is keyed by order.
/// </summary>
public static class PaymentServiceEndpoints
{
    /// <summary>
    /// Maps the service API.
    /// </summary>
    /// <param name="serviceToken">The shared secret every caller must present.</param>
    public static IEndpointRouteBuilder MapPaymentServiceEndpoints(
        this IEndpointRouteBuilder endpoints,
        string serviceToken)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceToken);

        // A literal, so the OpenAPI drift test can read it; that test pins it too.
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
        AuthorizePaymentRequest request,
        HttpContext http,
        IOrderPayments payments,
        CancellationToken cancellationToken)
    {
        var response = await payments
            .AuthorizeAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return response.Status switch
        {
            // Answers, not refusals: a retry gets back what it already has.
            AuthorizePaymentStatus.Authorized =>
                Ok(Outcomes.Authorized, response.PaymentId),
            AuthorizePaymentStatus.AlreadyCaptured =>
                Ok(Outcomes.AlreadyCaptured, response.PaymentId),

            AuthorizePaymentStatus.Declined => Refused(
                http, StatusCodes.Status402PaymentRequired,
                "Payment declined", Outcomes.Declined, response.PaymentId),

            // 504: the upstream gateway did not answer. The reconciler will settle it.
            AuthorizePaymentStatus.TimedOut => Refused(
                http, StatusCodes.Status504GatewayTimeout,
                "The gateway did not answer", Outcomes.TimedOut, response.PaymentId),

            AuthorizePaymentStatus.ConcurrentAttemptInFlight => Refused(
                http, StatusCodes.Status409Conflict,
                "Another attempt for this order is in flight",
                Outcomes.ConcurrentAttemptInFlight, response.PaymentId)
        };
    }

    /// <summary>Takes the funds this order's attempt is holding.</summary>
    private static async Task<IResult> CaptureAsync(
        CapturePaymentRequest request,
        HttpContext http,
        IOrderPayments payments,
        CancellationToken cancellationToken)
    {
        var response = await payments
            .CaptureAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return response.Status switch
        {
            CapturePaymentStatus.Captured => Ok(Outcomes.Captured, response.PaymentId),

            CapturePaymentStatus.TimedOut => Refused(
                http, StatusCodes.Status504GatewayTimeout,
                "The gateway did not answer", Outcomes.TimedOut, response.PaymentId),

            CapturePaymentStatus.NoAuthorization => Refused(
                http, StatusCodes.Status409Conflict,
                "There is nothing held for this order", Outcomes.NoAuthorization, response.PaymentId)
        };
    }

    /// <summary>Releases what this order's attempt is holding.</summary>
    private static async Task<IResult> VoidAsync(
        VoidPaymentRequest request,
        HttpContext http,
        IOrderPayments payments,
        CancellationToken cancellationToken)
    {
        var response = await payments
            .VoidAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return response.Status switch
        {
            VoidPaymentStatus.Voided => Ok(Outcomes.Voided, response.PaymentId),

            VoidPaymentStatus.TimedOut => Refused(
                http, StatusCodes.Status504GatewayTimeout,
                "The gateway did not answer", Outcomes.TimedOut, response.PaymentId),

            // Orders reads this as a lost race.
            VoidPaymentStatus.AlreadyCaptured => Refused(
                http, StatusCodes.Status409Conflict,
                "That attempt has already been captured", Outcomes.AlreadyCaptured, response.PaymentId),

            VoidPaymentStatus.NoAuthorization => Refused(
                http, StatusCodes.Status409Conflict,
                "There is nothing held for this order", Outcomes.NoAuthorization, response.PaymentId)
        };
    }

    private static IResult Ok(string outcome, Guid? paymentId) =>
        TypedResults.Ok(new PaymentOutcomeResponse(
            outcome,
            paymentId ?? throw new InvalidOperationException(
                $"A '{outcome}' outcome must carry the attempt it is about.")));

    /// <summary>
    /// A problem+json refusal with a <c>reason</c>, plus <c>paymentId</c> when there is one,
    /// because the caller reads it back.
    /// </summary>
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
