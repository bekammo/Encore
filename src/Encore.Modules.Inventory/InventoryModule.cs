using Encore.Modules.Inventory.Adapters.Caching;
using Encore.Modules.Inventory.Adapters.InProcess;
using Encore.Modules.Inventory.Adapters.Messaging;
using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Application;
using Encore.Modules.Inventory.Contracts;
using Encore.Modules.Inventory.Contracts.Events;
using Encore.Modules.Inventory.Endpoints;
using Encore.Modules.Inventory.Ports;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        //
        // TryAdd rather than Add: more than one module wants a clock now, and
        // three identical registrations of the same singleton are harmless right
        // up until a test replaces one, at which point last-registration-wins
        // silently decides which module got the fake.
        services.TryAddSingleton(TimeProvider.System);

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

        AddOutbox(services, configuration);

        // TODO: the expired-hold sweep (Phase 7) is still to come.
        return services;
    }

    /// <summary>
    /// Registers the outbox: what the published names mean, and the dispatcher that
    /// delivers them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The catalog is built here rather than discovered.</b> Scanning the assembly
    /// for handlers or contracts would make the published surface of this module an
    /// emergent property of what happens to be compiled in, and a typo'd name would
    /// present as an event that silently never arrives. Three explicit lines say what
    /// Inventory publishes, and adding a fourth event without one fails at the first
    /// save that raises it (<c>SeatEventPublication</c>).
    /// </para>
    /// <para>
    /// <b>The drain is not registered anywhere, and that is the design.</b> It lives
    /// in <c>InventoryDbContext.SaveChanges</c>, so it is on by construction for every
    /// write this module makes — there is no wiring to forget and no flag that can
    /// turn off the half of the outbox that has to be atomic. Only delivery is
    /// optional.
    /// </para>
    /// </remarks>
    private static void AddOutbox(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(OutboxOptions.SectionName);

        services.Configure<OutboxOptions>(section);

        services.AddSingleton(_ => new OutboxEventCatalog()
            .Register<SeatHeldV1>(InventoryEventTypes.SeatHeld)
            .Register<SeatReleasedV1>(InventoryEventTypes.SeatReleased)
            .Register<SeatSoldV1>(InventoryEventTypes.SeatSold));

        // Read once for the registration decision and bound separately for the
        // dispatcher's own use. On by default, unlike the migrator directly above:
        // a migrator that ran by default would rewrite a database as a side effect
        // of booting, while a dispatcher that did not would silently stop
        // delivering. Same question, opposite risk, opposite answer.
        var options = section.Get<OutboxOptions>() ?? new OutboxOptions();

        if (options.Enabled)
        {
            services.AddHostedService<OutboxDispatcher>();
        }
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
