using Encore.Modules.Inventory.Adapters.Caching;
using Encore.Modules.Inventory.Adapters.InProcess;
using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Application;
using Encore.Modules.Inventory.Contracts;
using Encore.Modules.Inventory.Endpoints;
using Encore.Modules.Inventory.Ports;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Encore.Modules.Inventory;

/// <summary>
/// The Inventory module's composition seam, and the one place where ports are
/// married to adapters. Nothing else in the solution knows that
/// <c>ISeatRepository</c> is EF Core or that <c>IDistributedLock</c> is Redis.
/// </summary>
public static class InventoryModule
{
    /// <summary>Registers the module's services.</summary>
    public static IServiceCollection AddInventoryModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<InventoryDbContext>(options =>
            options.UseInventoryNpgsql(
                configuration.GetConnectionString("Inventory")
                ?? throw new InvalidOperationException("Missing connection string 'Inventory'.")));

        // Named per module even though every module currently points at the same
        // database. The names are the seam: extracting Inventory later means
        // repointing a connection string, not editing code.
        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(
                configuration.GetConnectionString("Redis")
                ?? throw new InvalidOperationException("Missing connection string 'Redis'."));

            // Without this, Connect() throws when Redis is down — and it throws
            // from inside this factory, while DI is building the lock adapter.
            // No amount of exception translation in RedisDistributedLock can
            // catch that, because the adapter never gets constructed. The lock
            // is an optimisation, so a Redis that is absent must not stop the
            // host starting or a request being served: this makes Connect()
            // return a multiplexer that retries in the background, so individual
            // commands fail with a translatable exception instead.
            options.AbortOnConnectFail = false;
            options.ConnectTimeout = 1_000;
            options.ConnectRetry = 3;

            return ConnectionMultiplexer.Connect(options);
        });

        services.AddScoped<ISeatRepository, EfSeatRepository>();
        services.AddSingleton<IDistributedLock, RedisDistributedLock>();

        // The BCL clock. The domain never touches this — it takes the current
        // instant as a parameter — so this exists for the Application layer and
        // the expiry sweep only.
        services.AddSingleton(TimeProvider.System);

        services.AddScoped<HoldSeatCommandHandler>();
        services.AddScoped<SellSeatCommandHandler>();
        services.AddScoped<ReleaseSeatCommandHandler>();
        services.AddScoped<CreateSeatMapCommandHandler>();

        // The module's in-process front door, for callers that are other
        // modules rather than HTTP clients. This registration is the whole of
        // the extraction story: the day Inventory becomes its own service, this
        // line points at an HTTP-backed implementation instead and no consumer
        // is recompiled.
        services.AddScoped<ISeatReservations, InProcessSeatReservations>();

        // Off unless asked for. The run profiles set it so that a developer with
        // a fresh `docker compose up` gets a schema from `dotnet run`; anything
        // deployed applies migrations as its own deliberate step. The module
        // reads a setting rather than the environment name, because which
        // environment this is happens to be the host's business, not Inventory's.
        if (configuration.GetValue<bool>("Inventory:MigrateOnStartup"))
        {
            services.AddHostedService<InventoryMigrator>();
        }

        // TODO: the expired-hold sweep (Phase 7) is still to come. The outbox
        // dispatcher is a Soundcheck concern and is deliberately absent.
        return services;
    }

    /// <summary>Maps the module's HTTP surface.</summary>
    /// <remarks>
    /// Inventory went without one until now on the grounds that its surface
    /// should be designed alongside the hold and sell flow rather than ahead of
    /// it. That flow exists, so this does.
    /// </remarks>
    public static IEndpointRouteBuilder MapInventoryModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapSeatEndpoints();
        return endpoints;
    }
}
