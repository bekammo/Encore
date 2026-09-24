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

        // Singleton so a seeded Random is one sequence.
        services.AddSingleton<SimulatedPaymentGateway>();

        // TryAdd, so Orders' HTTP client wins whichever module registers first (018);
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

    private static void AddReconciliation(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(PaymentReconciliationOptions.SectionName);

        services.AddOptions<PaymentReconciliationOptions>()
            .Bind(section)
            .Validate(
                reconciliation => reconciliation.BatchSize > 0
                    && reconciliation.PollInterval > TimeSpan.Zero
                    && reconciliation.MinimumAge >= TimeSpan.Zero,
                $"{PaymentReconciliationOptions.SectionName}: BatchSize and PollInterval must be positive, and MinimumAge not negative.")
            .ValidateOnStart();

        var options = section.Get<PaymentReconciliationOptions>() ?? new PaymentReconciliationOptions();

        if (options.Enabled)
        {
            services.AddHostedService<PaymentReconciler>();
        }
    }

    public static IEndpointRouteBuilder MapPaymentsModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPaymentEndpoints();
        return endpoints;
    }

    /// <summary>
    /// A second seam, which only a host serving Payments out of process opts into (018). A missing
    /// service token fails startup rather than defaulting.
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
