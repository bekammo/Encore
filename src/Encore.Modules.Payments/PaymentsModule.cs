using Encore.Modules.Payments.Contracts;
using Encore.Modules.Payments.Data;
using Encore.Modules.Payments.Endpoints;
using Encore.Modules.Payments.Simulation;
using Encore.Modules.Shared.Persistence;
using Encore.Shared;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Encore.Modules.Payments;

/// <summary>The Payments module's composition seam.</summary>
public static class PaymentsModule
{
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

        // Singleton so a seeded Random is one sequence; the gateway's memory is a table.
        services.AddSingleton<SimulatedPaymentGateway>();

        // TryAdd, so Orders' HTTP client wins whichever module registers first.
        // StranglerSwitchTests pins all four orders.
        services.TryAddScoped<IOrderPayments, InProcessOrderPayments>();

        services.AddScoped<IReadinessCheck, PaymentsReadinessCheck>();

        services.TryAddSingleton(TimeProvider.System);

        if (configuration.GetValue<bool>("Payments:MigrateOnStartup"))
        {
            services.AddModuleMigrator<PaymentsDbContext>("Payments");
        }

        AddReconciliation(services, configuration);

        return services;
    }

    /// <summary>
    /// Registers the sweep that settles attempts the gateway never answered.
    /// </summary>
    private static void AddReconciliation(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(PaymentReconciliationOptions.SectionName);

        services.Configure<PaymentReconciliationOptions>(section);

        var options = section.Get<PaymentReconciliationOptions>() ?? new PaymentReconciliationOptions();

        if (options.Enabled)
        {
            services.AddHostedService<PaymentReconciler>();
        }
    }

    /// <summary>
    /// Maps the customer-facing routes, which are read-only. The write side is
    /// <see cref="MapPaymentsServiceApi"/>, which no customer can reach.
    /// </summary>
    public static IEndpointRouteBuilder MapPaymentsModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPaymentEndpoints();
        return endpoints;
    }

    /// <summary>
    /// Maps the service API Orders calls when Payments runs out of process. A separate seam,
    /// so a host must opt in; a missing service token fails startup rather than defaulting.
    /// </summary>
    public static IEndpointRouteBuilder MapPaymentsServiceApi(
        this IEndpointRouteBuilder endpoints,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var token = configuration["Payments:ServiceToken"];

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "Payments:ServiceToken must be set to map the Payments service API. "
                + "It is the only thing authenticating a call that can move money.");
        }

        endpoints.MapPaymentServiceEndpoints(token);
        return endpoints;
    }
}
