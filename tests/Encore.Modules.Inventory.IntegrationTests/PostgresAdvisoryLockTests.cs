using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Ports;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>
/// The advisory lock keeps the port's contract: try without waiting, one holder, released only
/// by its token, and a holder's session ending frees it.
/// </summary>
public sealed class PostgresAdvisoryLockTests : IAsyncLifetime
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("encore")
        .WithUsername("encore")
        .WithPassword("encore")
        .Build();

    private NpgsqlDataSource _dataSource = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _dataSource = NpgsqlDataSource.Create(_postgres.GetConnectionString());
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquire_WhenAnotherHoldsIt_ShouldNotWait()
    {
        var first = new PostgresAdvisoryLock(_dataSource);
        var second = new PostgresAdvisoryLock(_dataSource);

        var held = await first.TryAcquireAsync("client:1", Ttl);
        var refused = await second.TryAcquireAsync("client:1", Ttl);

        Assert.Equal(LockOutcome.Acquired, held.Outcome);
        Assert.Equal(LockOutcome.HeldByAnother, refused.Outcome);
    }

    [Fact]
    public async Task TryAcquire_ForAnotherResource_ShouldNotContend()
    {
        var advisory = new PostgresAdvisoryLock(_dataSource);

        await advisory.TryAcquireAsync("client:1", Ttl);
        var other = await advisory.TryAcquireAsync("client:2", Ttl);

        Assert.Equal(LockOutcome.Acquired, other.Outcome);
    }

    [Fact]
    public async Task Release_ShouldLetTheNextCallerIn()
    {
        var advisory = new PostgresAdvisoryLock(_dataSource);
        var held = await advisory.TryAcquireAsync("client:1", Ttl);

        Assert.True(await advisory.ReleaseAsync("client:1", held.Token!));

        Assert.Equal(LockOutcome.Acquired, (await advisory.TryAcquireAsync("client:1", Ttl)).Outcome);
    }

    [Fact]
    public async Task Release_WithAnUnknownToken_ShouldReleaseNothing()
    {
        var advisory = new PostgresAdvisoryLock(_dataSource);
        await advisory.TryAcquireAsync("client:1", Ttl);

        Assert.False(await advisory.ReleaseAsync("client:1", "not-a-token"));
        Assert.Equal(LockOutcome.HeldByAnother, (await advisory.TryAcquireAsync("client:1", Ttl)).Outcome);
    }

    /// <summary>A holder whose session dies frees the lock, which is what a TTL stands in for.</summary>
    [Fact]
    public async Task TryAcquire_AfterTheHoldersSessionEnds_ShouldSucceed()
    {
        var advisory = new PostgresAdvisoryLock(_dataSource);
        await advisory.TryAcquireAsync("client:1", Ttl);

        await using (var admin = await _dataSource.OpenConnectionAsync())
        await using (var kill = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_locks WHERE locktype = 'advisory'",
            admin))
        {
            await kill.ExecuteNonQueryAsync();
        }

        var fresh = new PostgresAdvisoryLock(NpgsqlDataSource.Create(_postgres.GetConnectionString()));

        Assert.Equal(LockOutcome.Acquired, (await fresh.TryAcquireAsync("client:1", Ttl)).Outcome);
    }
}
