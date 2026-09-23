using Encore.Modules.Inventory.Adapters.Caching;
using Encore.Modules.Inventory.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>
/// The Redis lock adapter against real Redis and against no Redis at all.
/// </summary>
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

    /// <summary>A stalled holder whose lock expired and was taken cannot free the new owner's lock.</summary>
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
    /// An unreachable Redis is reported as Unavailable, not thrown. Points at a closed port, so
    /// the shared container is not disturbed.
    /// </summary>
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

    /// <summary>An outage logs one warning, not one per attempt.</summary>
    [Fact]
    public async Task TryAcquire_WhenRedisStaysUnreachable_ShouldWarnOnceForTheWholeOutage()
    {
        var options = ConfigurationOptions.Parse("localhost:1");
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 250;
        options.ConnectRetry = 1;
        options.BacklogPolicy = BacklogPolicy.FailFast;

        await using var dead = await ConnectionMultiplexer.ConnectAsync(options);
        var logger = new CountingLogger();
        var deadLock = new RedisDistributedLock(dead, logger);

        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(LockOutcome.Unavailable, (await deadLock.TryAcquireAsync(NewResource(), Ttl)).Outcome);
        }

        Assert.False(await deadLock.ReleaseAsync(NewResource(), "token"));

        Assert.Equal(1, logger.Count(LogLevel.Warning));
        Assert.Equal(0, logger.Count(LogLevel.Information));
    }

    private sealed class CountingLogger : ILogger<RedisDistributedLock>
    {
        private readonly List<LogLevel> _levels = [];

        public int Count(LogLevel level) => _levels.Count(entry => entry == level);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        // Information and above, as a host at its default level would write.
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                lock (_levels)
                {
                    _levels.Add(logLevel);
                }
            }
        }
    }
}
