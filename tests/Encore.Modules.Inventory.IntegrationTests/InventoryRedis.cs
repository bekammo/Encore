using StackExchange.Redis;
using Testcontainers.Redis;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>Never flushed: every lock key a test takes names a fresh client, event or resource.</summary>
public sealed class InventoryRedis : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder("redis:7").Build();

    public IConnectionMultiplexer Connection { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _redis.StartAsync();

        Connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        await Connection.DisposeAsync();
        await _redis.DisposeAsync();
    }
}
