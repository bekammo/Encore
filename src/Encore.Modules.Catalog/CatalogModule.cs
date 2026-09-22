using Encore.Modules.Catalog.Contracts;
using Encore.Modules.Catalog.Data;
using Encore.Modules.Catalog.Endpoints;
using Encore.Modules.Shared.Persistence;
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
    /// <summary>Registers the module's services.</summary>
    public static IServiceCollection AddCatalogModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<CatalogDbContext>(options =>
            options.UseCatalogNpgsql(
                configuration.GetConnectionString("Catalog")
                ?? throw new InvalidOperationException("Missing connection string 'Catalog'.")));

        // The module's in-process front door, for callers that are other
        // modules rather than HTTP clients. This one line is the whole of the
        // extraction story: point it at an HTTP-backed implementation and no
        // consumer is recompiled.
        services.AddScoped<IEventPricing, InProcessEventPricing>();

        // Off unless asked for, exactly as Inventory's is. The run profiles set
        // it so a developer with a fresh `docker compose up` gets a schema from
        // `dotnet run`; anything deployed applies migrations as its own
        // deliberate step. Read as configuration rather than from
        // IHostEnvironment because which environment this is happens to be the
        // host's business, not Catalog's.
        if (configuration.GetValue<bool>("Catalog:MigrateOnStartup"))
        {
            services.AddModuleMigrator<CatalogDbContext>("Catalog");
        }

        return services;
    }

    /// <summary>Maps the module's HTTP surface.</summary>
    public static IEndpointRouteBuilder MapCatalogModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapCatalogEndpoints();
        return endpoints;
    }
}
