using Encore.Modules.Orders.Data;
using Encore.Modules.Orders.Endpoints;
using Encore.Modules.Payments.Contracts;
using Encore.Modules.Shared.Persistence;
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

        // The module's only service. Scoped because it holds a DbContext, and
        // concrete because nothing will ever substitute it — the two interfaces
        // it depends on are registered by the modules that own them, which is
        // the seam that survives either of them becoming remote.
        services.AddScoped<CheckoutService>();

        // Off unless asked for, exactly as the other modules. See DECISIONS 013.
        if (configuration.GetValue<bool>("Orders:MigrateOnStartup"))
        {
            services.AddModuleMigrator<OrdersDbContext>("Orders");
        }

        AddPaymentsClient(services, configuration);

        return services;
    }

    /// <summary>
    /// Chooses how Orders reaches Payments: in the same process, or over HTTP.
    /// DECISIONS 061.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the strangler's switch.</b> Left alone, nothing is registered here
    /// and Orders gets whatever <c>AddPaymentsModule</c> put in the container, which
    /// is <c>InProcessOrderPayments</c> — so the default is the monolith and the
    /// behaviour is exactly what it was. Set <c>Orders:Payments:BaseAddress</c> and
    /// this replaces that registration with one that talks to the service. Cutting
    /// over is a configuration change; cutting back is deleting it.
    /// </para>
    /// <para>
    /// <see cref="ServiceCollectionDescriptorExtensions.Replace"/> rather than a
    /// second <c>AddScoped</c>, so the outcome does not depend on whether Orders or
    /// Payments was registered first in <c>Program.cs</c>. Two registrations of one
    /// interface where last-wins decides which one moves money is not an arrangement
    /// worth having.
    /// </para>
    /// <para>
    /// <b>No Polly, deliberately.</b> Resilience is a later phase, and a retry policy
    /// here would be actively wrong today: <c>CheckoutService</c> already treats a
    /// timeout as a state rather than as a failure, and a transparent retry would
    /// turn one ambiguous answer into several without telling anybody. The timeout
    /// below is the whole policy.
    /// </para>
    /// </remarks>
    private static void AddPaymentsClient(IServiceCollection services, IConfiguration configuration)
    {
        var baseAddress = configuration["Orders:Payments:BaseAddress"];

        if (string.IsNullOrWhiteSpace(baseAddress))
        {
            return;
        }

        var token = configuration["Orders:Payments:ServiceToken"]
            ?? throw new InvalidOperationException(
                "Orders:Payments:ServiceToken must be set when Orders:Payments:BaseAddress is. "
                + "The Payments service API refuses a call that cannot present it.");

        var timeout = configuration.GetValue<TimeSpan?>("Orders:Payments:Timeout")
            ?? TimeSpan.FromSeconds(10);

        services.AddHttpClient<HttpOrderPayments>(client =>
        {
            // Trailing slash, because the adapter posts relative paths and
            // Uri resolution would otherwise drop the last segment of this one.
            client.BaseAddress = new Uri(
                baseAddress.EndsWith('/') ? baseAddress : baseAddress + "/",
                UriKind.Absolute);

            client.DefaultRequestHeaders.Add(PaymentsServiceApi.ServiceTokenHeader, token);
            client.Timeout = timeout;
        });

        services.Replace(
            ServiceDescriptor.Scoped<IOrderPayments>(provider =>
                provider.GetRequiredService<HttpOrderPayments>()));
    }

    /// <summary>Maps the module's HTTP surface.</summary>
    public static IEndpointRouteBuilder MapOrdersModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapOrderEndpoints();
        return endpoints;
    }
}
