using Encore.Modules.Payments.Endpoints;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Encore.Modules.Payments;

/// <summary>
/// The Payments module's single composition seam.
/// </summary>
public static class PaymentsModule
{
    /// <summary>Registers the module's services. Empty until there are any.</summary>
    public static IServiceCollection AddPaymentsModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // TODO: AddDbContext<PaymentsDbContext>(...), bind PaymentSimulationOptions
        // from configuration, register SimulatedPaymentGateway.
        return services;
    }

    /// <summary>Maps the module's HTTP surface.</summary>
    public static IEndpointRouteBuilder MapPaymentsModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPaymentEndpoints();
        return endpoints;
    }
}
