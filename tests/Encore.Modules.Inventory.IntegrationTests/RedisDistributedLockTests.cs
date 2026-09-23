using Encore.Modules.Inventory.Adapters.Caching;
using Encore.Modules.Inventory.Ports;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>
/// The Redis lock adapter against real Redis, and against no Redis at all.
/// </summary>
/// <remarks>
/// The check-and-delete release script is the sort of thing that is easy to get
/// subtly wrong and impossible to notice, and the unavailable path is the one
/// the architecture's central claim rests on — that correctness survives Redis
/// being gone. Neither can be proved with a fake, because a fake would be
/// asserting this test's own assumptions back at it.
/// </remarks>
public sealed class RedisDistributedLockTests : IAsyncLifetime
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    private readonly RedisContainer _redis = new RedisBuilder("redis:7").Build();

    private IConnectionMultiplexer _connection = null!;
    private RedisDistributedLock _lock = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _redis.StartAsync();
        _connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        _lock = new RedisDistributedLock(_connection, NullLogger<RedisDistributedLock>.Instance);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
        await _redis.DisposeAsync();
    }

    private static string NewResource() => $"test:{Guid.NewGuid():N}";

    [Fact]
    public async Task TryAcquire_WhenFree_ShouldAcquireWithAToken()
    {
        var acquisition = await _lock.TryAcquireAsync(NewResource(), Ttl);

        Assert.Equal(LockOutcome.Acquired, acquisition.Outcome);
        Assert.False(string.IsNullOrEmpty(acquisition.Token));
    }

    [Fact]
    public async Task TryAcquire_WhenSomebodyElseHoldsIt_ShouldReportHeldByAnother()
    {
        var resource = NewResource();
        await _lock.TryAcquireAsync(resource, Ttl);

        var second = await _lock.TryAcquireAsync(resource, Ttl);

        Assert.Equal(LockOutcome.HeldByAnother, second.Outcome);
        Assert.Null(second.Token);
    }

    [Fact]
    public async Task Release_WhenOwner_ShouldFreeTheLock()
    {
        var resource = NewResource();
        var acquisition = await _lock.TryAcquireAsync(resource, Ttl);

        Assert.True(await _lock.ReleaseAsync(resource, acquisition.Token!));
        Assert.Equal(LockOutcome.Acquired, (await _lock.TryAcquireAsync(resource, Ttl)).Outcome);
    }

    /// <summary>
    /// The race the Lua script exists to close. A stalled holder whose lock has
    /// already expired and been taken by somebody else must not be able to free
    /// the new owner's lock with a blind delete.
    /// </summary>
    [Fact]
    public async Task Release_WhenNotTheOwner_ShouldNotFreeSomebodyElsesLock()
    {
        var resource = NewResource();
        await _lock.TryAcquireAsync(resource, Ttl);

        Assert.False(await _lock.ReleaseAsync(resource, "a-token-that-was-never-ours"));

        // Still held by the original owner.
        Assert.Equal(LockOutcome.HeldByAnother, (await _lock.TryAcquireAsync(resource, Ttl)).Outcome);
    }

    /// <summary>
    /// The regression test for the defect that made "correctness survives Redis
    /// being gone" untrue: an unreachable Redis used to throw out of the handler
    /// and fail the whole hold. It must report itself and let the caller decide.
    /// </summary>
    /// <remarks>
    /// Points at a closed port rather than stopping the container, so it stays
    /// fast and cannot leave the shared container broken for other facts in this
    /// class. <c>AbortOnConnectFail = false</c> is what lets
    /// <see cref="ConnectionMultiplexer.Connect(ConfigurationOptions, TextWriter)"/>
    /// return at all against a dead endpoint — with the default, the throw
    /// happens here, before any adapter code could translate it, which is
    /// exactly how the defect hid in the DI container.
    /// </remarks>
    [Fact]
    public async Task TryAcquire_WhenRedisIsUnreachable_ShouldReportUnavailableRatherThanThrow()
    {
        var options = ConfigurationOptions.Parse("localhost:1");
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 250;
        options.ConnectRetry = 1;

        await using var dead = await ConnectionMultiplexer.ConnectAsync(options);
        var deadLock = new RedisDistributedLock(dead, NullLogger<RedisDistributedLock>.Instance);

        var acquisition = await deadLock.TryAcquireAsync(NewResource(), Ttl);

        Assert.Equal(LockOutcome.Unavailable, acquisition.Outcome);
        Assert.Null(acquisition.Token);
    }

    [Fact]
    public async Task Release_WhenRedisIsUnreachable_ShouldReportFailureRatherThanThrow()
    {
        var options = ConfigurationOptions.Parse("localhost:1");
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 250;
        options.ConnectRetry = 1;

        await using var dead = await ConnectionMultiplexer.ConnectAsync(options);
        var deadLock = new RedisDistributedLock(dead, NullLogger<RedisDistributedLock>.Instance);

        Assert.False(await deadLock.ReleaseAsync(NewResource(), "token"));
    }
}
