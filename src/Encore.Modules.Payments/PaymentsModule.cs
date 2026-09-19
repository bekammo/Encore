using Encore.Modules.Payments.Contracts;
using Encore.Modules.Payments.Data;
using Encore.Modules.Payments.Endpoints;
using Encore.Modules.Payments.Simulation;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Encore.Modules.Payments;

/// <summary>
/// The Payments module's single composition seam.
/// </summary>
/// <remarks>
/// The host knows these two methods and nothing else about this module, which is
/// what makes Soundcheck's extraction a change to one line rather than a project.
/// Payments is the module 004 nominated to be strangled first, so this seam is the
/// one most likely to be spent.
/// </remarks>
public static class PaymentsModule
{
    /// <summary>Registers the module's services.</summary>
    public static IServiceCollection AddPaymentsModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<PaymentsDbContext>(options =>
            options.UsePaymentsNpgsql(
                configuration.GetConnectionString("Payments")
                ?? throw new InvalidOperationException("Missing connection string 'Payments'.")));

        services.Configure<PaymentSimulationOptions>(
            configuration.GetSection(PaymentSimulationOptions.SectionName));

        // Singleton, because the gateway's memory of which idempotency keys it has
        // already answered is the point of it. A scoped instance would forget
        // between requests, and the retry path would then pass tests it should
        // fail.
        services.AddSingleton<SimulatedPaymentGateway>();

        services.AddScoped<IOrderPayments, InProcessOrderPayments>();

        services.TryAddSingleton(TimeProvider.System);

        if (configuration.GetValue<bool>("Payments:MigrateOnStartup"))
        {
            services.AddHostedService<PaymentsMigrator>();
        }

        return services;
    }

    /// <summary>Maps the module's HTTP surface.</summary>
    public static IEndpointRouteBuilder MapPaymentsModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPaymentEndpoints();
        return endpoints;
    }
}
