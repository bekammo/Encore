using System.Text.Json;
using Encore.Modules.Inventory.Adapters.Messaging;
using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>
/// What happens to an outbox row after it has been delivered, and what the module
/// says about the ones that have not been. <c>DECISIONS.md</c> 070.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two subjects in one class because they are two questions about one table</b>,
/// and a container apiece would double this file's cost to say so. The retention
/// sweep removes delivered rows; the readiness check counts the rows nobody has
/// managed to deliver. Neither is load-bearing for any seat invariant, which is the
/// property the first half of this file is really about.
/// </para>
/// <para>
/// A fake clock throughout, so "thirty days ago" is a fact rather than a wait.
/// </para>
/// </remarks>
public sealed class OutboxHousekeepingTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("encore")
        .WithUsername("encore")
        .WithPassword("encore")
        .Build();

    private string _connectionString = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();

        await using var context = Context();
        await context.Database.MigrateAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    // -- Retention --------------------------------------------------------

    /// <summary>
    /// A delivered message older than the window goes; one delivered yesterday
    /// stays.
    /// </summary>
    /// <remarks>
    /// 051 refused to build this on the grounds that nothing had an opinion about
    /// how long an event is worth keeping. 070 supersedes that on one point only:
    /// "forever" was an opinion too, and nobody had chosen it either.
    /// </remarks>
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

    /// <summary>
    /// An undelivered message is work and a dead letter is evidence. Neither is
    /// deleted by age, however old it gets.
    /// </summary>
    /// <remarks>
    /// The important half of this file. A retention sweep that went by
    /// <c>OccurredAt</c> would quietly destroy the record of the one message that
    /// never arrived — which is precisely the thing somebody eventually comes
    /// looking for, and which 051 asked to be made visible rather than disposable.
    /// </remarks>
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

    /// <summary>
    /// One pass removes at most a batch, so a first run against a table nobody has
    /// ever pruned is a series of small deletes rather than one enormous one.
    /// </summary>
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

    /// <summary>
    /// The count 051 asked to be visible somewhere and nothing surfaced: a message
    /// that has stopped being retried.
    /// </summary>
    /// <remarks>
    /// <b>And the module stays ready.</b> One <c>SeatSold</c> that never reached
    /// Notifications is a thing to look at; taking the host out of rotation for it
    /// would turn a message nobody read into a request path nobody can reach.
    /// </remarks>
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

    /// <summary>
    /// A database it cannot read is the one thing that does make this module
    /// unready, and the check answers rather than throwing — an exception escaping
    /// here would make the readiness endpoint itself the thing that is down.
    /// </summary>
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

    private InventoryDbContext Context() =>
        new(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInventoryNpgsql(_connectionString)
            .Options);

    /// <summary>
    /// One outbox row in whatever state the test needs, written the way the drain
    /// writes it and then moved on with the aggregate's own methods.
    /// </summary>
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

    private SweeperHost Host(Action<OutboxRetentionOptions>? configure = null)
    {
        var options = new OutboxRetentionOptions();
        configure?.Invoke(options);

        var services = new ServiceCollection();

        services.AddDbContext<InventoryDbContext>(builder =>
            builder.UseInventoryNpgsql(_connectionString));

        var provider = services.BuildServiceProvider();

        var sweeper = new OutboxRetentionSweeper(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            new FixedTimeProvider(Now),
            NullLogger<OutboxRetentionSweeper>.Instance);

        return new SweeperHost(provider, sweeper);
    }

    private sealed class SweeperHost(ServiceProvider provider, OutboxRetentionSweeper sweeper)
        : IAsyncDisposable
    {
        internal OutboxRetentionSweeper Sweeper { get; } = sweeper;

        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }

    /// <summary>A clock that does not move, so retention is data rather than duration.</summary>
    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
