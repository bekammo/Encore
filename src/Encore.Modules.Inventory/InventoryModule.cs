using Encore.Modules.Inventory.Adapters.Caching;
using Encore.Modules.Inventory.Adapters.InProcess;
using Encore.Modules.Inventory.Adapters.Messaging;
using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Adapters.Scheduling;
using Encore.Modules.Inventory.Application;
using Encore.Modules.Inventory.Contracts;
using Encore.Modules.Inventory.Contracts.Events;
using Encore.Modules.Inventory.Endpoints;
using Encore.Modules.Inventory.Ports;
using Encore.Modules.Shared.Persistence;
using Encore.Shared;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using StackExchange.Redis;

namespace Encore.Modules.Inventory;

/// <summary>
/// The Inventory module's composition seam: the one place ports are wired to adapters.
/// </summary>
public static class InventoryModule
{
    public static IServiceCollection AddInventoryModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<InventoryDbContext>(options =>
            options.UseInventoryNpgsql(
                configuration.GetConnectionString("Inventory")
                ?? throw new InvalidOperationException("Missing connection string 'Inventory'.")));

        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(
                configuration.GetConnectionString("Redis")
                ?? throw new InvalidOperationException("Missing connection string 'Redis'."));

            // The lock is optional, so a missing Redis must not stop the host starting.
            // Without this, Connect() throws inside this factory, before the lock
            // adapter exists to translate anything.
            options.AbortOnConnectFail = false;
            // Overridable only for chaos-rig experiments.
            options.ConnectTimeout = configuration.GetValue("Inventory:RedisLock:ConnectTimeoutMs", 1_000);
            options.ConnectRetry = 3;
            // Refuse commands immediately while disconnected instead of queueing them
            // for a reconnect, which cost about a second per lock attempt.
            options.BacklogPolicy = BacklogPolicy.FailFast;
            // One healthy round trip. An outage answers "unavailable" quickly; it does
            // not change what the lock means.
            options.SyncTimeout = 250;
            options.AsyncTimeout = 250;

            return ConnectionMultiplexer.Connect(options);
        });

        services.AddScoped<ISeatRepository, EfSeatRepository>();

        AddHoldCapLock(services, configuration);

        // TryAdd: several modules register a clock, and a test replacing one must win.
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<HoldSeatCommandHandler>();
        services.AddScoped<SellSeatCommandHandler>();
        services.AddScoped<ReleaseSeatCommandHandler>();
        services.AddScoped<CreateSeatMapCommandHandler>();

        // The front door for other modules. Extracting Inventory means swapping this
        // registration for an HTTP-backed one.
        services.AddScoped<ISeatReservations, InProcessSeatReservations>();

        services.AddScoped<IReadinessCheck, InventoryReadinessCheck>();

        // Off unless the run profile asks for it; deployments migrate explicitly.
        if (configuration.GetValue<bool>("Inventory:MigrateOnStartup"))
        {
            services.AddModuleMigrator<InventoryDbContext>("Inventory");
        }

        AddOutbox(services, configuration);
        AddExpiredHoldSweep(services, configuration);

        return services;
    }

    /// <summary>
    /// Registers the lock that serialises the hold cap's count (005). Redis by default;
    /// <c>Inventory:HoldCapLock = Postgres</c> takes an advisory lock instead, so the two can
    /// be measured against each other. Singletons: the cooldown's window and the advisory
    /// lock's held sessions are per process.
    /// </summary>
    private static void AddHoldCapLock(IServiceCollection services, IConfiguration configuration)
    {
        if (string.Equals(configuration["Inventory:HoldCapLock"], "Postgres", StringComparison.OrdinalIgnoreCase))
        {
            // Its own data source, so a held lock's session never waits behind the request's
            // queries for a pooled connection. The connection budget is in docker-compose.yml.
            services.AddSingleton<IDistributedLock>(_ => new PostgresAdvisoryLock(
                NpgsqlDataSource.Create(
                    configuration.GetConnectionString("Inventory")
                    ?? throw new InvalidOperationException("Missing connection string 'Inventory'."))));

            return;
        }

        var redisLock = configuration.GetSection(RedisLockOptions.SectionName).Get<RedisLockOptions>()
            ?? new RedisLockOptions();

        services.AddSingleton<RedisDistributedLock>();
        services.AddSingleton<IDistributedLock>(provider => new CooldownDistributedLock(
            provider.GetRequiredService<RedisDistributedLock>(),
            redisLock.Cooldown,
            provider.GetRequiredService<TimeProvider>()));
    }

    /// <summary>
    /// Registers the lapsed-hold sweep. On by default; switching it off must change
    /// nothing an invariant depends on.
    /// </summary>
    private static void AddExpiredHoldSweep(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(ExpiredHoldSweepOptions.SectionName);

        services.Configure<ExpiredHoldSweepOptions>(section);

        var options = section.Get<ExpiredHoldSweepOptions>() ?? new ExpiredHoldSweepOptions();

        if (options.Enabled)
        {
            services.AddHostedService<ExpiredHoldSweeper>();
        }
    }

    /// <summary>
    /// Registers the published event names and the dispatcher. The drain itself lives in
    /// <c>InventoryDbContext.SaveChanges</c>, so it cannot be switched off.
    /// </summary>
    private static void AddOutbox(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(OutboxOptions.SectionName);

        services.Configure<OutboxOptions>(section);

        services.AddSingleton(_ => new OutboxEventCatalog()
            .Register<SeatHeldV1>(InventoryEventTypes.SeatHeld)
            .Register<SeatReleasedV1>(InventoryEventTypes.SeatReleased)
            .Register<SeatSoldV1>(InventoryEventTypes.SeatSold));

        // On by default: a dispatcher that did not run would silently stop delivering.
        var options = section.Get<OutboxOptions>() ?? new OutboxOptions();

        if (options.Enabled)
        {
            services.AddHostedService<OutboxDispatcher>();
        }

        AddOutboxRetention(services, configuration);
    }

    /// <summary>
    /// Registers the job that deletes old delivered messages. Has its own flag so turning
    /// the dispatcher off does not also stop pruning.
    /// </summary>
    private static void AddOutboxRetention(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(OutboxRetentionOptions.SectionName);

        services.Configure<OutboxRetentionOptions>(section);

        var options = section.Get<OutboxRetentionOptions>() ?? new OutboxRetentionOptions();

        if (options.Enabled)
        {
            services.AddHostedService<OutboxRetentionSweeper>();
        }
    }

    public static IEndpointRouteBuilder MapInventoryModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapSeatEndpoints();
        return endpoints;
    }
}
