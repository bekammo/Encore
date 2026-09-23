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
            // Overridable so 073's discriminating experiment could be run: move
            // this alone and see whether the cost of a lock attempt against a
            // missing Redis moves with it. 074 ran it at 1,000 and 3,000 — the
            // per-lock cost stayed at ~1,000ms either way, and every failure was
            // RedisConnectionException rather than RedisTimeoutException. That
            // rules this setting out as the mechanism; 074 has the numbers.
            // Nothing outside the chaos rig should set this — 1,000 is 064's
            // tuned value and the knob stays for the next experiment this class
            // of question needs.
            options.ConnectTimeout = configuration.GetValue("Inventory:RedisLock:ConnectTimeoutMs", 1_000);
            options.ConnectRetry = 3;
            // The candidate fix 073 named and deliberately did not apply in the
            // same session that found it: a disconnected multiplexer's default
            // behaviour is to queue a command in a backlog and wait for a
            // reconnect, which is what a ~1,000ms cost independent of
            // ConnectTimeout looks like. FailFast refuses immediately instead.
            // Applied here, in the session after the one that ruled out
            // ConnectTimeout, on the same rule that section followed: change and
            // measurement do not share a session. This session's redis fault run
            // is 1,000/3,000 ConnectTimeout with the default backlog policy, so
            // this line is untested by anything in 074 — the next chaos session
            // against `redis` measures it. See DECISIONS.md 074.
            options.BacklogPolicy = BacklogPolicy.FailFast;

            // The command timeouts, and they are set here for a reason 064 had to
            // measure before anybody could see it. ConnectTimeout above was tuned
            // and these two were left at StackExchange.Redis's 5s default, so a
            // hold — which takes two locks — spent about ten seconds discovering
            // twice that the lock was unavailable before any database work began:
            // med 11,979ms against 140ms with Redis up, an 85x cost for a
            // dependency the design says is optional.
            //
            // 250ms rather than something smaller, because this is the budget for
            // a single round trip to a healthy Redis on the same network, and a
            // lock that gives up on an ordinary GC pause would report contention
            // that is not there. A holder that has genuinely gone away costs half
            // a second across both locks now instead of ten.
            //
            // This does not change what the lock means. Unavailable is still "I
            // don't know", the attempt still proceeds, and xmin still decides
            // (010). It changes only how long that answer takes to arrive.
            options.SyncTimeout = 250;
            options.AsyncTimeout = 250;

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

        // This module's half of /health/ready. Scoped because it reads through the
        // scoped context, and registered as the interface so the host can ask every
        // module the same question without learning which modules exist. 070.
        services.AddScoped<IReadinessCheck, InventoryReadinessCheck>();

        // Off unless asked for. The run profiles set it so that a developer with
        // a fresh `docker compose up` gets a schema from `dotnet run`; anything
        // deployed applies migrations as its own deliberate step. The module
        // reads a setting rather than the environment name, because which
        // environment this is happens to be the host's business, not Inventory's.
        if (configuration.GetValue<bool>("Inventory:MigrateOnStartup"))
        {
            services.AddModuleMigrator<InventoryDbContext>("Inventory");
        }

        AddOutbox(services, configuration);
        AddExpiredHoldSweep(services, configuration);

        return services;
    }

    /// <summary>
    /// Registers the background sweep that tidies lapsed holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Registered on exactly the same terms as the dispatcher, and that is the
    /// point of the symmetry.</b> On by default, because a cleanup job that did not
    /// run by default would silently stop cleaning; switchable off, because 007's
    /// falsifiability test requires that switching it off changes nothing an
    /// invariant depends on.
    /// </para>
    /// <para>
    /// <b>No lease, no single-owner flag, and no <c>FOR UPDATE SKIP LOCKED</c>.</b>
    /// Two of these racing over one seat is arbitrated by <c>xmin</c> like every
    /// other write in this module, and the loser writes nothing — see
    /// <see cref="ExpiredHoldSweeper"/>. 061's single-owner debt is
    /// <c>PaymentReconciler</c>'s alone, because that job's expensive half is a call
    /// to a gateway that no database token can arbitrate.
    /// </para>
    /// </remarks>
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

        AddOutboxRetention(services, configuration);
    }

    /// <summary>
    /// Registers the job that removes delivered messages once they are older than
    /// the retention window. <c>DECISIONS.md</c> 070, superseding 051 on this point.
    /// </summary>
    /// <remarks>
    /// Separate from the dispatcher's own flag on purpose. Delivery and tidying are
    /// different duties on different clocks — one second against one hour — and an
    /// operator switching the dispatcher off to measure it (056) should not silently
    /// stop the table being pruned as well.
    /// </remarks>
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
