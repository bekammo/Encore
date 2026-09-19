using Encore.Modules.Orders.Data;
using Encore.Modules.Orders.Endpoints;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Encore.Modules.Orders;

/// <summary>
/// The Orders module's single composition seam.
/// </summary>
public static class OrdersModule
{
    /// <summary>Registers the module's services.</summary>
    public static IServiceCollection AddOrdersModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<OrdersDbContext>(options =>
            options.UseOrdersNpgsql(
                configuration.GetConnectionString("Orders")
                ?? throw new InvalidOperationException("Missing connection string 'Orders'.")));

        // Orders reads a clock to stamp orders and to judge the on-sale gate.
        // TryAdd because Inventory registers the same system clock, and the last
        // registration would otherwise win silently.
        services.TryAddSingleton(TimeProvider.System);

        // Off unless asked for, exactly as the other modules. See DECISIONS 013.
        if (configuration.GetValue<bool>("Orders:MigrateOnStartup"))
        {
            services.AddHostedService<OrdersMigrator>();
        }

        return services;
    }

    /// <summary>Maps the module's HTTP surface.</summary>
    public static IEndpointRouteBuilder MapOrdersModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapOrderEndpoints();
        return endpoints;
    }
}
