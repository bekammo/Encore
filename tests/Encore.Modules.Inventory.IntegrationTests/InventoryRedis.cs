using StackExchange.Redis;
using Testcontainers.Redis;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>
/// One Redis per test class. Never flushed: every lock key a test takes names a fresh client,
/// event or resource, so no test can meet another's key.
/// </summary>
public sealed class InventoryRedis : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder("redis:7").Build();

    /// <summary>A connection to the container, shared by the class's tests.</summary>
    public IConnectionMultiplexer Connection { get; private set; } = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _redis.StartAsync();

        Connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await Connection.DisposeAsync();
        await _redis.DisposeAsync();
    }
}
