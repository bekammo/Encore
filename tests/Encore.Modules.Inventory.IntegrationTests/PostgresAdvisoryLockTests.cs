using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Ports;
using Npgsql;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>
/// Most tests leave "client:1" held by a session nothing closes, so each test first ends every
/// session holding an advisory lock and takes a data source of its own.
/// </summary>
public sealed class PostgresAdvisoryLockTests(InventoryDatabase database)
    : IClassFixture<InventoryDatabase>, IAsyncLifetime
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    // The 5000 waits up to 5s for each backend to exit, so its locks are gone on return.
    private const string EndLockHoldersSql = """
        SELECT pg_terminate_backend(pid, 5000)
        FROM (SELECT DISTINCT pid FROM pg_locks WHERE locktype = 'advisory') AS holders
        """;

    private readonly InventoryDatabase _database = database;

    private NpgsqlDataSource _dataSource = null!;

    public async Task InitializeAsync()
    {
        _dataSource = NpgsqlDataSource.Create(_database.ConnectionString);

        await using var admin = await _dataSource.OpenConnectionAsync();
        await using var endHolders = new NpgsqlCommand(EndLockHoldersSql, admin);

        await endHolders.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _dataSource.DisposeAsync();

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

    [Fact]
    public async Task TryAcquire_AfterTheHoldersSessionEnds_ShouldSucceed()
    {
        var advisory = new PostgresAdvisoryLock(_dataSource);
        await advisory.TryAcquireAsync("client:1", Ttl);

        await using (var admin = await _dataSource.OpenConnectionAsync())
        await using (var kill = new NpgsqlCommand(EndLockHoldersSql, admin))
        {
            await kill.ExecuteNonQueryAsync();
        }

        var fresh = new PostgresAdvisoryLock(NpgsqlDataSource.Create(_database.ConnectionString));

        Assert.Equal(LockOutcome.Acquired, (await fresh.TryAcquireAsync("client:1", Ttl)).Outcome);
    }
}
