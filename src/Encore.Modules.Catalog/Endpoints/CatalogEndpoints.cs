using Microsoft.AspNetCore.Routing;

namespace Encore.Modules.Catalog.Endpoints;

/// <summary>
/// Minimal API endpoints for browsing events and venues. Straight CRUD against
/// <see cref="Data.CatalogDbContext"/> — no handler indirection, because there
/// is no behaviour here worth indirecting.
/// </summary>
public static class CatalogEndpoints
{
    /// <summary>Maps the /catalog route group. No routes defined yet.</summary>
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // TODO: group = endpoints.MapGroup("/catalog"); GET /events, GET /events/{id}, ...
        return endpoints;
    }
}
