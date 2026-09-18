using Encore.Modules.Inventory.Adapters.Caching;
using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Ports;
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
            options.UseNpgsql(configuration.GetConnectionString("Inventory")));

        // Named per module even though every module currently points at the same
        // database. The names are the seam: extracting Inventory later means
        // repointing a connection string, not editing code.
        services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(
                configuration.GetConnectionString("Redis")
                ?? throw new InvalidOperationException("Missing connection string 'Redis'.")));

        services.AddScoped<ISeatRepository, EfSeatRepository>();
        services.AddSingleton<IDistributedLock, RedisDistributedLock>();

        // The BCL clock. The domain never touches this — it takes the current
        // instant as a parameter — so this exists for the Application layer and
        // the expiry sweep only.
        services.AddSingleton(TimeProvider.System);

        // TODO: HoldSeatCommandHandler (Phase 5) and the expired-hold sweep
        // (Phase 7). The outbox dispatcher is a Soundcheck concern and is
        // deliberately absent.
        return services;
    }
}
