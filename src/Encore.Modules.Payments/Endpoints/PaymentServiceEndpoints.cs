using Encore.Modules.Payments.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

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

        // A literal, not PaymentsServiceApi.Prefix, so the OpenAPI drift test can read it.
        // PaymentsServiceApiTests keeps the two in step.
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
            // Answers, not refusals: a retry gets back what it already has.
            AuthorizePaymentStatus.Authorized =>
                Ok("authorized", response.PaymentId),
            AuthorizePaymentStatus.AlreadyCaptured =>
                Ok("already_captured", response.PaymentId),

            AuthorizePaymentStatus.Declined => Refused(
                http, StatusCodes.Status402PaymentRequired,
                "Payment declined", "declined", response.PaymentId),

            // 504: the upstream gateway did not answer. The reconciler will settle it.
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

            // Orders reads this as a lost race.
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
