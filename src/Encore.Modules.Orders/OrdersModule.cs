using Encore.Modules.Orders.Data;
using Encore.Modules.Orders.Endpoints;
using Encore.Modules.Payments.Contracts;
using Encore.Modules.Shared.Http;
using Encore.Modules.Shared.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Encore.Modules.Orders;

public static class OrdersModule
{
    public static IServiceCollection AddOrdersModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<OrdersDbContext>(options =>
            options.UseOrdersNpgsql(
                configuration.GetConnectionString("Orders")
                ?? throw new InvalidOperationException("Missing connection string 'Orders'.")));

        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<CheckoutService>();

        services.AddPerIpRateLimitPolicy(configuration, OrderEndpoints.CheckoutRateLimitPolicy);

        if (configuration.GetValue<bool>("Orders:MigrateOnStartup"))
        {
            services.AddModuleMigrator<OrdersDbContext>("Orders");
        }

        AddPaymentsClient(services, configuration);
        AddCaptureSweep(services, configuration);
        AddOrderExpirySweep(services, configuration);

        return services;
    }

    private static void AddOrderExpirySweep(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(OrderExpirySweepOptions.SectionName);

        services.AddOptions<OrderExpirySweepOptions>()
            .Bind(section)
            .Validate(
                sweep => sweep.BatchSize > 0
                    && sweep.PollInterval > TimeSpan.Zero
                    && sweep.Grace >= TimeSpan.Zero,
                $"{OrderExpirySweepOptions.SectionName}: BatchSize and PollInterval must be positive, and Grace not negative.")
            .ValidateOnStart();

        var options = section.Get<OrderExpirySweepOptions>() ?? new OrderExpirySweepOptions();

        if (options.Enabled)
        {
            services.AddHostedService<OrderExpirySweeper>();
        }
    }

    private static void AddCaptureSweep(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(CaptureSweepOptions.SectionName);

        services.AddOptions<CaptureSweepOptions>()
            .Bind(section)
            .Validate(
                sweep => sweep.BatchSize > 0
                    && sweep.PollInterval > TimeSpan.Zero
                    && sweep.MinimumAge >= TimeSpan.Zero,
                $"{CaptureSweepOptions.SectionName}: BatchSize and PollInterval must be positive, and MinimumAge not negative.")
            .ValidateOnStart();

        var options = section.Get<CaptureSweepOptions>() ?? new CaptureSweepOptions();

        if (options.Enabled)
        {
            services.AddHostedService<CaptureSweeper>();
        }
    }

    // The strangler switch (018): Replace wins over Payments' TryAdd in either registration
    // order. No retry policy: a timeout is already a handled state.
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

        // A request that never connected never reached the gateway, so waiting out the whole
        // timeout for it tells nothing more. With the service down, every confirm did.
        var connectTimeout = configuration.GetValue<TimeSpan?>("Orders:Payments:ConnectTimeout")
            ?? TimeSpan.FromSeconds(1);

        services.AddHttpClient<HttpOrderPayments>(client =>
            {
                // Trailing slash, or relative paths would drop the last segment.
                client.BaseAddress = new Uri(
                    baseAddress.EndsWith('/') ? baseAddress : baseAddress + "/",
                    UriKind.Absolute);

                client.DefaultRequestHeaders.Add(PaymentsServiceApi.ServiceTokenHeader, token);
                client.Timeout = timeout;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { ConnectTimeout = connectTimeout });

        services.Replace(
            ServiceDescriptor.Scoped<IOrderPayments>(provider =>
                provider.GetRequiredService<HttpOrderPayments>()));
    }

    public static IEndpointRouteBuilder MapOrdersModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapOrderEndpoints();
        return endpoints;
    }
}
