using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Encore.Modules.Inventory.Adapters.Messaging;
using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Contracts.Events;
using Encore.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>
/// Delivery: at-least-once, in id order, claimed by one dispatcher at a time, and
/// bounded when a handler keeps refusing.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class builds a DI container, which no other test here does</b>, and the
/// exception is the point rather than a lapse. Everything else in this suite
/// constructs its subject by hand because its subject is a class; the dispatcher's
/// job is largely scope management and handler resolution, so a hand-built instance
/// would test everything except the part that can actually be wrong.
/// </para>
/// <para>
/// <b>One tick is driven directly rather than by starting the hosted service.</b> A
/// test that called <c>StartAsync</c> and waited would be timing-dependent, and a
/// flaky test about an at-least-once mechanism is worse than no test — its failure
/// is indistinguishable from the bug it exists to catch.
/// </para>
/// </remarks>
public sealed class OutboxDispatcherTests : IAsyncLifetime
{
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

        await using var context = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>()
                .UseInventoryNpgsql(_connectionString)
                .Options);

        await context.Database.MigrateAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task Dispatch_ShouldDeliverTheMessageAndMarkItProcessed()
    {
        var seatId = Guid.NewGuid();
        var messageId = await SeedSoldAsync(seatId);

        var handler = new RecordingHandler();
        await using var host = Host(handler);

        var claimed = await host.Dispatcher.DispatchBatchAsync(CancellationToken.None);

        Assert.Equal(1, claimed);

        var delivered = Assert.Single(handler.Delivered);
        Assert.Equal(messageId, delivered.MessageId);
        Assert.Equal(seatId, delivered.Event.SeatId);

        var stored = Assert.Single(await MessagesAsync());
        Assert.NotNull(stored.ProcessedAt);
        Assert.Equal(0, stored.Attempts);
        Assert.Null(stored.LastError);
    }

    [Fact]
    public async Task Dispatch_WhenNothingIsDue_ShouldClaimNothing()
    {
        var handler = new RecordingHandler();
        await using var host = Host(handler);

        Assert.Equal(0, await host.Dispatcher.DispatchBatchAsync(CancellationToken.None));
        Assert.Empty(handler.Delivered);
    }

    [Fact]
    public async Task Dispatch_ShouldNotClaimAMessageItHasAlreadyDelivered()
    {
        await SeedSoldAsync(Guid.NewGuid());

        var handler = new RecordingHandler();
        await using var host = Host(handler);

        Assert.Equal(1, await host.Dispatcher.DispatchBatchAsync(CancellationToken.None));
        Assert.Equal(0, await host.Dispatcher.DispatchBatchAsync(CancellationToken.None));

        Assert.Single(handler.Delivered);
    }

    /// <summary>Messages reach handlers in the order their ids were assigned.</summary>
    [Fact]
    public async Task Dispatch_ShouldDeliverInIdOrder()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();

        await SeedSoldAsync(first);
        await SeedSoldAsync(second);
        await SeedSoldAsync(third);

        var handler = new RecordingHandler();
        await using var host = Host(handler);

        await host.Dispatcher.DispatchBatchAsync(CancellationToken.None);

        Assert.Equal(
            [first, second, third],
            handler.Delivered.Select(delivery => delivery.SeatId).ToArray());
    }

    /// <summary>
    /// A handler that throws leaves the message undelivered, counted, and scheduled
    /// for later — not marked processed, and not lost.
    /// </summary>
    [Fact]
    public async Task Dispatch_WhenTheHandlerThrows_ShouldRecordTheFailureAndBackOff()
    {
        await SeedSoldAsync(Guid.NewGuid());

        var handler = new RecordingHandler { Fail = true };
        await using var host = Host(handler);

        Assert.Equal(1, await host.Dispatcher.DispatchBatchAsync(CancellationToken.None));

        var stored = Assert.Single(await MessagesAsync());

        Assert.Null(stored.ProcessedAt);
        Assert.Equal(1, stored.Attempts);
        Assert.NotNull(stored.LastError);
        Assert.Contains("handler refused", stored.LastError, StringComparison.Ordinal);

        // Backed off into the future, so the very next tick does not immediately
        // burn a second attempt on it.
        Assert.True(stored.NextAttemptAt > stored.OccurredAt);
    }

    /// <summary>
    /// A failing message does not block the ones behind it.
    /// </summary>
    /// <remarks>
    /// This is the cost of the design stated as a test: the queue keeps moving, and
    /// the price is that a backed-off message is overtaken. Blocking instead would
    /// preserve a global order nothing has asked for, at the price of one bad row
    /// stopping every good one.
    /// </remarks>
    [Fact]
    public async Task Dispatch_WhenOneMessageFails_ShouldStillDeliverTheRest()
    {
        var poison = Guid.NewGuid();
        var good = Guid.NewGuid();

        await SeedSoldAsync(poison);
        await SeedSoldAsync(good);

        var handler = new RecordingHandler { FailFor = poison };
        await using var host = Host(handler);

        Assert.Equal(2, await host.Dispatcher.DispatchBatchAsync(CancellationToken.None));

        var messages = await MessagesAsync();

        Assert.Null(messages[0].ProcessedAt);
        Assert.Equal(1, messages[0].Attempts);
        Assert.NotNull(messages[1].ProcessedAt);
    }

    /// <summary>
    /// A handler that overruns its deadline is failed like any other handler, and
    /// the tick carries on.
    /// </summary>
    /// <remarks>
    /// The bound 064's fourth fault showed was missing. Delivery happens inside the
    /// claim transaction, so before <c>DECISIONS.md</c> 069 a consumer blocked on a
    /// table lock held that transaction — and the batch's row locks — for as long as
    /// it was blocked, which in that run was twenty seconds. The message backs off
    /// and is retried; what does not happen is the rest of the system waiting for it.
    /// </remarks>
    [Fact]
    public async Task Dispatch_WhenAHandlerOverrunsItsDeadline_ShouldFailThatMessageAndCarryOn()
    {
        var slow = Guid.NewGuid();

        await SeedSoldAsync(slow);

        var handler = new RecordingHandler { Stall = TimeSpan.FromSeconds(30) };

        await using var host = Host(
            handler,
            options => options.DeliveryTimeout = TimeSpan.FromMilliseconds(100));

        var started = Stopwatch.StartNew();

        Assert.Equal(1, await host.Dispatcher.DispatchBatchAsync(CancellationToken.None));

        // The point of the whole change: the tick took the deadline, not the stall.
        Assert.True(
            started.Elapsed < TimeSpan.FromSeconds(10),
            $"The tick waited {started.Elapsed} on a handler it had given 100ms.");

        var stored = Assert.Single(await MessagesAsync());

        Assert.Null(stored.ProcessedAt);
        Assert.Equal(1, stored.Attempts);
        Assert.Empty(handler.Delivered);
    }

    /// <summary>
    /// When the tick's own budget runs out it commits what it delivered and leaves
    /// the rest untouched for the next one.
    /// </summary>
    /// <remarks>
    /// The other half of 069's bound. A per-message deadline alone still allows a
    /// batch of fifty to hold one transaction open for fifty deadlines; this is what
    /// makes the worst case a number somebody chose. A message the tick never reached
    /// is not failed and not counted against its attempts — it was never tried.
    /// </remarks>
    [Fact]
    public async Task Dispatch_WhenTheBatchBudgetRunsOut_ShouldLeaveTheRestForTheNextTick()
    {
        for (var seat = 0; seat < 3; seat++)
        {
            await SeedSoldAsync(Guid.NewGuid());
        }

        var handler = new RecordingHandler { Stall = TimeSpan.FromMilliseconds(120) };

        await using var host = Host(
            handler,
            options =>
            {
                options.DeliveryTimeout = TimeSpan.FromSeconds(5);
                options.MaxBatchDuration = TimeSpan.FromMilliseconds(100);
            });

        // Claimed three, delivered one, and stopped: the budget is spent once the
        // first handler has taken longer than all of it.
        Assert.Equal(3, await host.Dispatcher.DispatchBatchAsync(CancellationToken.None));

        var afterFirst = await MessagesAsync();

        Assert.Single(afterFirst, message => message.ProcessedAt is not null);
        Assert.Equal(2, afterFirst.Count(message => message.ProcessedAt is null && message.Attempts == 0));

        // Untouched means claimable, so the next tick picks them up normally.
        await using var patient = Host(new RecordingHandler());

        Assert.Equal(2, await patient.Dispatcher.DispatchBatchAsync(CancellationToken.None));
        Assert.All(await MessagesAsync(), message => Assert.NotNull(message.ProcessedAt));
    }

    /// <summary>
    /// Past its attempt budget a message stops being claimed, and sits there as a
    /// dead letter rather than being retried forever or deleted.
    /// </summary>
    [Fact]
    public async Task Dispatch_WhenAMessageExhaustsItsAttempts_ShouldStopClaimingIt()
    {
        await SeedSoldAsync(Guid.NewGuid());

        var handler = new RecordingHandler { Fail = true };

        // Two attempts and no backoff, so the budget is reachable inside a test
        // without waiting for a real delay.
        await using var host = Host(
            handler,
            options =>
            {
                options.MaxAttempts = 2;
                options.BaseBackoff = TimeSpan.Zero;
                options.MaxBackoff = TimeSpan.Zero;
            });

        Assert.Equal(1, await host.Dispatcher.DispatchBatchAsync(CancellationToken.None));
        Assert.Equal(1, await host.Dispatcher.DispatchBatchAsync(CancellationToken.None));

        // Budget spent: no longer claimable.
        Assert.Equal(0, await host.Dispatcher.DispatchBatchAsync(CancellationToken.None));

        var stored = Assert.Single(await MessagesAsync());

        Assert.Equal(2, stored.Attempts);
        Assert.Null(stored.ProcessedAt);
        Assert.Equal(2, handler.Delivered.Count + handler.Failures);
    }

    /// <summary>
    /// Two dispatchers over one table deliver each message exactly once.
    /// </summary>
    /// <remarks>
    /// <c>FOR UPDATE SKIP LOCKED</c> is the whole mechanism: each claim passes over
    /// the rows the other already holds rather than waiting on them. Without it the
    /// two would either block each other or, worse, both read the same rows and
    /// deliver them twice. There is one host today, and there will be more the day
    /// this deploys.
    /// </remarks>
    [Fact]
    public async Task Dispatch_WhenTwoDispatchersRunTogether_ShouldDeliverEachMessageOnce()
    {
        const int Messages = 20;

        for (var i = 0; i < Messages; i++)
        {
            await SeedSoldAsync(Guid.NewGuid());
        }

        var handler = new RecordingHandler();

        // One batch each, so neither can take the lot before the other starts.
        await using var left = Host(handler, options => options.BatchSize = Messages / 2);
        await using var right = Host(handler, options => options.BatchSize = Messages / 2);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runs = new[]
        {
            Task.Run(async () =>
            {
                await gate.Task;
                return await left.Dispatcher.DispatchBatchAsync(CancellationToken.None);
            }),
            Task.Run(async () =>
            {
                await gate.Task;
                return await right.Dispatcher.DispatchBatchAsync(CancellationToken.None);
            })
        };

        gate.SetResult();
        var claimed = await Task.WhenAll(runs);

        // Neither delivered anything twice, and between them they covered the batch.
        var seatIds = handler.Delivered.Select(delivery => delivery.SeatId).ToList();

        Assert.Equal(seatIds.Count, seatIds.Distinct().Count());
        Assert.Equal(claimed.Sum(), seatIds.Count);

        var processed = (await MessagesAsync()).Count(message => message.ProcessedAt is not null);
        Assert.Equal(seatIds.Count, processed);
    }

    /// <summary>
    /// Writes one <c>SeatSold</c> outbox row directly and returns its message id.
    /// </summary>
    /// <remarks>
    /// Seeded rather than produced by holding and selling a seat, because what is
    /// under test here is delivery. <c>OutboxDrainTests</c> is where the rows are
    /// proven to arrive from real transitions; conflating the two would make every
    /// failure in this file ambiguous about which half broke.
    /// </remarks>
    private async Task<Guid> SeedSoldAsync(Guid seatId)
    {
        var occurredAt = DateTime.UtcNow;

        var payload = JsonSerializer.Serialize(
            new SeatSoldV1(seatId, Guid.NewGuid(), Guid.NewGuid(), occurredAt),
            SeatEventPublication.SerializerOptions);

        var message = OutboxMessage.For(
            Guid.CreateVersion7(),
            InventoryEventTypes.SeatSold,
            payload,
            occurredAt);

        await using var context = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>()
                .UseInventoryNpgsql(_connectionString)
                .Options);

        context.OutboxMessages.Add(message);
        await context.SaveChangesAsync();

        return message.MessageId;
    }

    private async Task<List<OutboxMessage>> MessagesAsync()
    {
        await using var context = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>()
                .UseInventoryNpgsql(_connectionString)
                .Options);

        return await context.OutboxMessages.AsNoTracking()
            .OrderBy(message => message.Id)
            .ToListAsync();
    }

    private DispatcherHost Host(
        IIntegrationEventHandler<SeatSoldV1> handler,
        Action<OutboxOptions>? configure = null)
    {
        var options = new OutboxOptions();
        configure?.Invoke(options);

        var services = new ServiceCollection();

        services.AddDbContext<InventoryDbContext>(builder =>
            builder.UseInventoryNpgsql(_connectionString));

        // The same instance for the dispatcher and for the assertions, so what the
        // test reads is what the handler actually saw.
        services.AddSingleton(handler);

        var provider = services.BuildServiceProvider();

        var dispatcher = new OutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new OutboxEventCatalog().Register<SeatSoldV1>(InventoryEventTypes.SeatSold),
            Options.Create(options),
            TimeProvider.System,
            NullLogger<OutboxDispatcher>.Instance);

        return new DispatcherHost(provider, dispatcher);
    }

    private sealed class DispatcherHost(ServiceProvider provider, OutboxDispatcher dispatcher)
        : IAsyncDisposable
    {
        internal OutboxDispatcher Dispatcher { get; } = dispatcher;

        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }

    /// <summary>
    /// Hand-written, like every other fake in this repo — there is no mocking
    /// library here and this needs none.
    /// </summary>
    /// <remarks>
    /// Thread-safe because one test runs two dispatchers against one instance, and a
    /// <c>List</c> torn by concurrent adds would fail that test for the wrong reason.
    /// </remarks>
    private sealed class RecordingHandler : IIntegrationEventHandler<SeatSoldV1>
    {
        private readonly ConcurrentQueue<Delivery> _delivered = new();
        private int _failures;

        /// <summary>Refuse everything.</summary>
        internal bool Fail { get; init; }

        /// <summary>Refuse only the message about this seat.</summary>
        internal Guid? FailFor { get; init; }

        /// <summary>
        /// Take this long before answering — a consumer blocked on a lock, a slow
        /// query, or a dependency that has stopped answering. DECISIONS 069.
        /// </summary>
        internal TimeSpan Stall { get; init; }

        internal IReadOnlyList<Delivery> Delivered => [.. _delivered];

        internal int Failures => Volatile.Read(ref _failures);

        public async Task HandleAsync(
            SeatSoldV1 integrationEvent,
            Guid messageId,
            CancellationToken cancellationToken)
        {
            if (Stall > TimeSpan.Zero)
            {
                // The token is honoured, as a handler doing real work would honour
                // it: the dispatcher's deadline arrives as a cancellation, and a
                // handler that ignored it would be testing nothing about the
                // deadline and everything about Task.Delay.
                await Task.Delay(Stall, cancellationToken);
            }

            if (Fail || FailFor == integrationEvent.SeatId)
            {
                Interlocked.Increment(ref _failures);

                throw new InvalidOperationException("handler refused");
            }

            _delivered.Enqueue(new Delivery(messageId, integrationEvent));
        }

        /// <summary>
        /// Both halves of what a handler is given. The id is a parameter rather than
        /// a field on the payload, so recording only the payload would leave the
        /// deduplication key — the thing consumers actually depend on — untested.
        /// </summary>
        internal sealed record Delivery(Guid MessageId, SeatSoldV1 Event)
        {
            internal Guid SeatId => Event.SeatId;
        }
    }
}
