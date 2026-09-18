using Microsoft.AspNetCore.Routing;

namespace Encore.Modules.Orders.Endpoints;

/// <summary>
/// Minimal API endpoints for placing and reading back orders. An order here is
/// a record of what was bought; the hard part (does the seat exist, is it
/// still free) belongs to Inventory, not to this module.
/// </summary>
public static class OrderEndpoints
{
    /// <summary>Maps the /orders route group. No routes defined yet.</summary>
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // TODO: group = endpoints.MapGroup("/orders"); POST /, GET /{id}, ...
        return endpoints;
    }
}
