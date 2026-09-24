using System.Text.Json;
using Encore.Modules.Inventory.Adapters.Messaging;
using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>
/// After delivery: the retention sweep removes old delivered rows, and the readiness check
/// counts the rows nobody managed to deliver. A fixed clock throughout. Both count the whole
/// outbox, so the shared database is emptied before each test.
/// </summary>
public sealed class OutboxHousekeepingTests(InventoryDatabase database)
    : IClassFixture<InventoryDatabase>, IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

    private readonly InventoryDatabase _database = database;

    /// <summary>Empties the outbox the previous test left.</summary>
    public Task InitializeAsync() => _database.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    // -- Retention --------------------------------------------------------

    /// <summary>A delivered message older than the window goes; one delivered yesterday stays.</summary>
    [Fact]
    public async Task Sweep_ShouldDeleteDeliveredMessagesPastTheWindowAndKeepTheRest()
    {
        var ancient = await SeedAsync(deliveredAt: Now.AddDays(-31));
        var recent = await SeedAsync(deliveredAt: Now.AddDays(-1));

        await using var host = Host();

        Assert.Equal(1, await host.Sweeper.SweepBatchAsync(CancellationToken.None));

        var left = await MessageIdsAsync();

        Assert.DoesNotContain(ancient, left);
        Assert.Contains(recent, left);
    }

    /// <summary>Undelivered messages and dead letters are never deleted, however old.</summary>
    [Fact]
    public async Task Sweep_ShouldNeverDeleteAMessageThatWasNotDelivered()
    {
        var pending = await SeedAsync(deliveredAt: null, occurredAt: Now.AddDays(-400));
        var deadLettered = await SeedAsync(deliveredAt: null, occurredAt: Now.AddDays(-400), attempts: 5);

        await using var host = Host();

        Assert.Equal(0, await host.Sweeper.SweepBatchAsync(CancellationToken.None));

        var left = await MessageIdsAsync();

        Assert.Contains(pending, left);
        Assert.Contains(deadLettered, left);
    }

    /// <summary>One pass removes at most a batch.</summary>
    [Fact]
    public async Task Sweep_ShouldDeleteAtMostOneBatch()
    {
        for (var message = 0; message < 5; message++)
        {
            await SeedAsync(deliveredAt: Now.AddDays(-31));
        }

        await using var host = Host(options => options.BatchSize = 2);

        Assert.Equal(2, await host.Sweeper.SweepBatchAsync(CancellationToken.None));
        Assert.Equal(2, await host.Sweeper.SweepBatchAsync(CancellationToken.None));
        Assert.Equal(1, await host.Sweeper.SweepBatchAsync(CancellationToken.None));
        Assert.Equal(0, await host.Sweeper.SweepBatchAsync(CancellationToken.None));
    }

    // -- Readiness --------------------------------------------------------

    /// <summary>Dead letters are counted, and the module stays ready.</summary>
    [Fact]
    public async Task Readiness_ShouldReportTheBacklogWithoutFailing()
    {
        await SeedAsync(deliveredAt: null);
        await SeedAsync(deliveredAt: null, attempts: 5);
        await SeedAsync(deliveredAt: Now.AddMinutes(-1));

        await using var context = Context();

        var check = new InventoryReadinessCheck(context, Options.Create(new OutboxOptions()));
        var result = await check.CheckAsync(CancellationToken.None);

        Assert.True(result.Ready);
        Assert.Contains("1 pending", result.Detail, StringComparison.Ordinal);
        Assert.Contains("1 dead-lettered", result.Detail, StringComparison.Ordinal);
    }

    /// <summary>An unreadable database makes the module unready, and the check answers rather than throws.</summary>
    [Fact]
    public async Task Readiness_WhenTheDatabaseIsUnreachable_ShouldFailRatherThanThrow()
    {
        await using var context = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>()
                .UseInventoryNpgsql("Host=127.0.0.1;Port=1;Database=encore;Username=encore;Password=encore;Timeout=1")
                .Options);

        var check = new InventoryReadinessCheck(context, Options.Create(new OutboxOptions()));
        var result = await check.CheckAsync(CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Contains("unreachable", result.Detail, StringComparison.Ordinal);
    }

    // -- Plumbing ---------------------------------------------------------

    private InventoryDbContext Context() => new(_database.Options);

    /// <summary>One outbox row in the state a test needs, moved on with its own methods.</summary>
    private async Task<Guid> SeedAsync(
        DateTime? deliveredAt,
        DateTime? occurredAt = null,
        int attempts = 0)
    {
        var raisedAt = occurredAt ?? Now.AddDays(-40);

        var payload = JsonSerializer.Serialize(
            new SeatSoldV1(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), raisedAt),
            SeatEventPublication.SerializerOptions);

        var message = OutboxMessage.For(
            Guid.CreateVersion7(),
            InventoryEventTypes.SeatSold,
            payload,
            raisedAt);

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            message.MarkFailed(raisedAt, "handler refused", TimeSpan.Zero);
        }

        if (deliveredAt is { } processed)
        {
            message.MarkProcessed(processed);
        }

        await using var context = Context();

        context.OutboxMessages.Add(message);
        await context.SaveChangesAsync();

        return message.MessageId;
    }

    private async Task<List<Guid>> MessageIdsAsync()
    {
        await using var context = Context();

        return await context.OutboxMessages
            .AsNoTracking()
            .Select(message => message.MessageId)
            .ToListAsync();
    }

    /// <summary>
    /// The sweep over its own container, with a clock that does not move, so retention is data
    /// rather than duration.
    /// </summary>
    private SweeperHost Host(Action<OutboxRetentionOptions>? configure = null)
    {
        var options = new OutboxRetentionOptions();
        configure?.Invoke(options);

        var services = new ServiceCollection();

        services.AddDbContext<InventoryDbContext>(builder =>
            builder.UseInventoryNpgsql(_database.ConnectionString));

        var provider = services.BuildServiceProvider();

        var sweeper = new OutboxRetentionSweeper(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            new FakeTimeProvider(Now),
            NullLogger<OutboxRetentionSweeper>.Instance);

        return new SweeperHost(provider, sweeper);
    }

    private sealed class SweeperHost(ServiceProvider provider, OutboxRetentionSweeper sweeper)
        : IAsyncDisposable
    {
        internal OutboxRetentionSweeper Sweeper { get; } = sweeper;

        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }
}
