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
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using StackExchange.Redis;

namespace Encore.Modules.Inventory;

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

            // A missing Redis must not stop the host (004): without this, Connect() throws here,
            // before the lock adapter exists to translate anything.
            options.AbortOnConnectFail = false;
            // Overridden only by the chaos rig.
            options.ConnectTimeout = configuration.GetValue("Inventory:RedisLock:ConnectTimeoutMs", 1_000);
            options.ConnectRetry = 3;
            // Queueing commands for a reconnect cost about a second per lock attempt.
            options.BacklogPolicy = BacklogPolicy.FailFast;
            // One healthy round trip, so an outage answers Unavailable quickly.
            options.SyncTimeout = 250;
            options.AsyncTimeout = 250;

            return ConnectionMultiplexer.Connect(options);
        });

        services.AddScoped<ISeatRepository, EfSeatRepository>();

        AddHoldCapLock(services, configuration);

        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<HoldSeatCommandHandler>();
        services.AddScoped<SellSeatCommandHandler>();
        services.AddScoped<ReleaseSeatCommandHandler>();
        services.AddScoped<CreateSeatMapCommandHandler>();

        services.AddScoped<ISeatReservations, InProcessSeatReservations>();

        // Every registered check votes on /health/ready; the host never learns which modules
        // have a database (016, 029).
        services.AddHealthChecks().AddCheck<InventoryReadinessCheck>(InventoryReadinessCheck.Name);

        if (configuration.GetValue<bool>("Inventory:MigrateOnStartup"))
        {
            services.AddModuleMigrator<InventoryDbContext>("Inventory");
        }

        AddOutbox(services, configuration);
        AddExpiredHoldSweep(services, configuration);

        return services;
    }

    // Postgres keeps the cap through a Redis outage at the throughput cost 005 measured, so
    // Redis stays the default. Singletons: the cooldown window and the advisory lock's sessions
    // are per process.
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

        // After Redis fails to answer, callers are told Unavailable for this long without asking
        // it, and proceed without the lock as they would have anyway. Zero asks Redis every time.
        var cooldown = configuration.GetValue("Inventory:RedisLock:Cooldown", TimeSpan.FromSeconds(1));

        services.AddSingleton<RedisDistributedLock>();
        services.AddSingleton<IDistributedLock>(provider => new CooldownDistributedLock(
            provider.GetRequiredService<RedisDistributedLock>(),
            cooldown,
            provider.GetRequiredService<TimeProvider>()));
    }

    private static void AddExpiredHoldSweep(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(ExpiredHoldSweepOptions.SectionName);

        services.AddOptions<ExpiredHoldSweepOptions>()
            .Bind(section)
            .Validate(
                sweep => sweep.BatchSize > 0 && sweep.PollInterval > TimeSpan.Zero,
                $"{ExpiredHoldSweepOptions.SectionName}: BatchSize and PollInterval must be positive.")
            .ValidateOnStart();

        var options = section.Get<ExpiredHoldSweepOptions>() ?? new ExpiredHoldSweepOptions();

        if (options.Enabled)
        {
            services.AddHostedService<ExpiredHoldSweeper>();
        }
    }

    // The drain lives in InventoryDbContext.SaveChanges, so it cannot be switched off (015).
    private static void AddOutbox(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(OutboxOptions.SectionName);

        services.AddOptions<OutboxOptions>()
            .Bind(section)
            .Validate(
                outbox => outbox.BatchSize > 0
                    && outbox.PollInterval > TimeSpan.Zero
                    && outbox.MaxAttempts > 0
                    && outbox.DeliveryTimeout > TimeSpan.Zero
                    && outbox.MaxBatchDuration > TimeSpan.Zero
                    && outbox.BaseBackoff >= TimeSpan.Zero
                    && outbox.MaxBackoff >= outbox.BaseBackoff,
                $"{OutboxOptions.SectionName}: sizes, attempts, intervals and timeouts must be positive, and MaxBackoff at least a non-negative BaseBackoff.")
            .ValidateOnStart();

        services.AddSingleton(_ => new OutboxEventCatalog()
            .Register<SeatHeldV1>(InventoryEventTypes.SeatHeld)
            .Register<SeatReleasedV1>(InventoryEventTypes.SeatReleased)
            .Register<SeatSoldV1>(InventoryEventTypes.SeatSold));

        var options = section.Get<OutboxOptions>() ?? new OutboxOptions();

        if (options.Enabled)
        {
            services.AddHostedService<OutboxDispatcher>();
        }

        AddOutboxRetention(services, configuration);
    }

    private static void AddOutboxRetention(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(OutboxRetentionOptions.SectionName);

        services.AddOptions<OutboxRetentionOptions>()
            .Bind(section)
            .Validate(
                retention => retention.BatchSize > 0
                    && retention.PollInterval > TimeSpan.Zero
                    && retention.KeepDelivered >= TimeSpan.Zero,
                $"{OutboxRetentionOptions.SectionName}: BatchSize and PollInterval must be positive, and KeepDelivered not negative.")
            .ValidateOnStart();

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
