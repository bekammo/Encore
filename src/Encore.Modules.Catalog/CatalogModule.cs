using Encore.Modules.Catalog.Endpoints;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Encore.Modules.Catalog;

/// <summary>
/// The Catalog module's single composition seam. The host knows these two
/// methods and nothing else about this module.
/// </summary>
public static class CatalogModule
{
    /// <summary>Registers the module's services. Empty until there are any.</summary>
    public static IServiceCollection AddCatalogModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // TODO: AddDbContext<CatalogDbContext>(...) against the "Catalog" connection string.
        return services;
    }

    /// <summary>Maps the module's HTTP surface.</summary>
    public static IEndpointRouteBuilder MapCatalogModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapCatalogEndpoints();
        return endpoints;
    }
}
