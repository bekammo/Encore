using Encore.Modules.Payments.Contracts;
using Encore.Modules.Payments.Data;
using Encore.Modules.Payments.Endpoints;
using Encore.Modules.Payments.Simulation;
using Encore.Modules.Shared.Persistence;
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
            services.AddModuleMigrator<PaymentsDbContext>("Payments");
        }

        AddReconciliation(services, configuration);

        return services;
    }

    /// <summary>
    /// Registers the sweep that settles attempts the gateway never answered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read once for the registration decision and bound separately for the
    /// reconciler's own use, the same shape <c>InventoryModule.AddOutbox</c> uses
    /// and for the same reason: whether a hosted service exists at all is a
    /// composition-time question, and rebinding it at resolve time would be
    /// pretending it could change.
    /// </para>
    /// <para>
    /// <b>No port, and no interface over the sweep.</b> It reads this module's own
    /// table and calls this module's own gateway, and nothing will ever substitute
    /// it — which is 001's test for whether an abstraction has earned its place.
    /// It sits in <c>Data/</c> beside <c>PaymentsMigrator</c>, the module's other
    /// hosted service, rather than in a folder invented for it.
    /// </para>
    /// </remarks>
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

    /// <summary>Maps the module's HTTP surface.</summary>
    public static IEndpointRouteBuilder MapPaymentsModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPaymentEndpoints();
        return endpoints;
    }
}
