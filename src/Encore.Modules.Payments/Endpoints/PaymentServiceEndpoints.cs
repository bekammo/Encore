using Encore.Modules.Payments.Contracts;
using Encore.Modules.Shared.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Outcomes = Encore.Modules.Payments.Contracts.PaymentsServiceApi.Outcomes;

namespace Encore.Modules.Payments.Endpoints;

/// <summary>
/// Not customer-facing: a service token instead of <c>X-Client-Id</c>, and an <c>/internal</c>
/// prefix an ingress can refuse. RPC-shaped because the seam is keyed by order (018).
/// </summary>
public static class PaymentServiceEndpoints
{
    public static IEndpointRouteBuilder MapPaymentServiceEndpoints(
        this IEndpointRouteBuilder endpoints,
        string serviceToken)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceToken);

        // A literal: the OpenAPI drift test reads it from source.
        var service = endpoints
            .MapGroup("/internal/payments")
            .AddEndpointFilter(new SharedSecretEndpointFilter(
                PaymentsServiceApi.ServiceTokenHeader,
                serviceToken,
                title: "Unauthenticated service call",
                detail: $"The {PaymentsServiceApi.ServiceTokenHeader} header is required and must be the configured service token.",
                reason: "service_token_invalid"));

        service.MapPost("/authorize", AuthorizeAsync);
        service.MapPost("/capture", CaptureAsync);
        service.MapPost("/void", VoidAsync);

        return endpoints;
    }

    private static async Task<IResult> AuthorizeAsync(
        AuthorizePaymentRequest request,
        HttpContext context,
        IOrderPayments payments,
        CancellationToken cancellationToken)
    {
        var response = await payments
            .AuthorizeAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return response.Status switch
        {
            AuthorizePaymentStatus.Authorized =>
                Ok(Outcomes.Authorized, response.PaymentId),
            AuthorizePaymentStatus.AlreadyCaptured =>
                Ok(Outcomes.AlreadyCaptured, response.PaymentId),

            AuthorizePaymentStatus.Declined => Refused(
                context, StatusCodes.Status402PaymentRequired,
                "Payment declined", Outcomes.Declined, response.PaymentId),

            AuthorizePaymentStatus.TimedOut => Refused(
                context, StatusCodes.Status504GatewayTimeout,
                "The gateway did not answer", Outcomes.TimedOut, response.PaymentId),

            AuthorizePaymentStatus.ConcurrentAttemptInFlight => Refused(
                context, StatusCodes.Status409Conflict,
                "Another attempt for this order is in flight",
                Outcomes.ConcurrentAttemptInFlight, response.PaymentId)
        };
    }

    private static async Task<IResult> CaptureAsync(
        CapturePaymentRequest request,
        HttpContext context,
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
                context, StatusCodes.Status504GatewayTimeout,
                "The gateway did not answer", Outcomes.TimedOut, response.PaymentId),

            CapturePaymentStatus.NoAuthorization => Refused(
                context, StatusCodes.Status409Conflict,
                "There is nothing held for this order", Outcomes.NoAuthorization, response.PaymentId),

            CapturePaymentStatus.Declined => Refused(
                context, StatusCodes.Status402PaymentRequired,
                "Capture declined", Outcomes.Declined, response.PaymentId)
        };
    }

    private static async Task<IResult> VoidAsync(
        VoidPaymentRequest request,
        HttpContext context,
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
                context, StatusCodes.Status504GatewayTimeout,
                "The gateway did not answer", Outcomes.TimedOut, response.PaymentId),

            VoidPaymentStatus.AlreadyCaptured => Refused(
                context, StatusCodes.Status409Conflict,
                "That attempt has already been captured", Outcomes.AlreadyCaptured, response.PaymentId),

            VoidPaymentStatus.NoAuthorization => Refused(
                context, StatusCodes.Status409Conflict,
                "There is nothing held for this order", Outcomes.NoAuthorization, response.PaymentId)
        };
    }

    private static IResult Ok(string outcome, Guid? paymentId) =>
        TypedResults.Ok(new PaymentOutcomeResponse(
            outcome,
            paymentId ?? throw new InvalidOperationException(
                $"A '{outcome}' outcome must carry the attempt it is about.")));

    // The caller reads paymentId back from refusals too.
    private static IResult Refused(
        HttpContext context,
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
            instance: context.Request.Path,
            extensions: extensions);
    }
}
