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

        // TryAdd, not Add, and the difference is the whole Strangler Fig.
        //
        // 061 registered this with Add and argued in OrdersModule that
        // ServiceCollectionDescriptorExtensions.Replace made the outcome
        // independent of which module Program.cs registers first. It does not.
        // Replace removes the first existing registration and appends its own, so
        // with Orders registered before Payments — which is the order in
        // Encore.Api — there was nothing to remove when the HTTP client was
        // registered, and this line then appended the in-process adapter after it.
        // Last-wins handed every call to InProcessOrderPayments, and the
        // strangled configuration was quietly still a monolith.
        //
        // The chaos harness is what found it: run 3 stopped payments-api and the
        // confirms kept succeeding, with captured rows appearing in a database no
        // running process was supposed to be writing to. DECISIONS 063.
        //
        // TryAdd makes the pair order-independent for real. Orders first: this
        // sees the HTTP registration and stands down. Payments first: this
        // registers and Orders' Replace takes it out. No BaseAddress at all:
        // Orders registers nothing and this is the only candidate, which is the
        // monolith, unchanged.
        services.TryAddScoped<IOrderPayments, InProcessOrderPayments>();

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

    /// <summary>Maps the module's customer-facing HTTP surface.</summary>
    /// <remarks>
    /// Read-only, and deliberately so: DECISIONS 033. The write side is
    /// <see cref="MapPaymentsServiceApi"/>, which this does not call and which no
    /// customer can reach.
    /// </remarks>
    public static IEndpointRouteBuilder MapPaymentsModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPaymentEndpoints();
        return endpoints;
    }

    /// <summary>
    /// Maps the service API that Orders calls when Payments is out of process.
    /// DECISIONS 061.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A second seam rather than a flag on the first, because the two surfaces have
    /// different audiences and different authentication, and a host should have to
    /// say which of them it is opening. The Payments host calls both; the monolith
    /// calls only <see cref="MapPaymentsModule"/> unless it is deliberately standing
    /// in for the Payments service.
    /// </para>
    /// <para>
    /// <b>Missing configuration throws rather than defaults.</b> A service token with
    /// a fallback value is a service token everybody has, and the failure mode of
    /// getting this wrong is an open authorise endpoint. Refusing to start is the
    /// cheapest possible way to find out.
    /// </para>
    /// </remarks>
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
