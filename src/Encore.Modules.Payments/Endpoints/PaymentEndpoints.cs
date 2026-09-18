using Microsoft.AspNetCore.Routing;

namespace Encore.Modules.Payments.Endpoints;

/// <summary>
/// Minimal API endpoints for taking a payment against an order and reading its
/// outcome back.
/// </summary>
public static class PaymentEndpoints
{
    /// <summary>Maps the /payments route group. No routes defined yet.</summary>
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // TODO: group = endpoints.MapGroup("/payments"); POST /, GET /{id}, ...
        return endpoints;
    }
}
