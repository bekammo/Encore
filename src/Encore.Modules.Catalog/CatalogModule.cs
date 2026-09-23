using Encore.Modules.Catalog.Contracts;
using Encore.Modules.Catalog.Data;
using Encore.Modules.Catalog.Endpoints;
using Encore.Modules.Shared.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Encore.Modules.Catalog;

/// <summary>
/// The Catalog module's composition seam.
/// </summary>
public static class CatalogModule
{
    public static IServiceCollection AddCatalogModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<CatalogDbContext>(options =>
            options.UseCatalogNpgsql(
                configuration.GetConnectionString("Catalog")
                ?? throw new InvalidOperationException("Missing connection string 'Catalog'.")));

        // The front door for other modules; extraction swaps this for an HTTP client.
        services.AddScoped<IEventPricing, InProcessEventPricing>();

        // Off unless the run profile asks for it; deployments migrate explicitly.
        if (configuration.GetValue<bool>("Catalog:MigrateOnStartup"))
        {
            services.AddModuleMigrator<CatalogDbContext>("Catalog");
        }

        return services;
    }

    public static IEndpointRouteBuilder MapCatalogModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapCatalogEndpoints();
        return endpoints;
    }
}
