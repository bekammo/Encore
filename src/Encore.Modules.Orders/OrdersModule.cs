using Encore.Modules.Orders.Endpoints;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Encore.Modules.Orders;

/// <summary>
/// The Orders module's single composition seam.
/// </summary>
public static class OrdersModule
{
    /// <summary>Registers the module's services. Empty until there are any.</summary>
    public static IServiceCollection AddOrdersModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // TODO: AddDbContext<OrdersDbContext>(...) against the "Orders" connection string.
        return services;
    }

    /// <summary>Maps the module's HTTP surface.</summary>
    public static IEndpointRouteBuilder MapOrdersModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapOrderEndpoints();
        return endpoints;
    }
}
