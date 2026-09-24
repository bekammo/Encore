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

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>A tick claims whatever is due, so the database is emptied before each test.</summary>
public sealed class OutboxDispatcherTests(InventoryDatabase database)
    : IClassFixture<InventoryDatabase>, IAsyncLifetime
{
    private readonly InventoryDatabase _database = database;

    public Task InitializeAsync() => _database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

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

    /// <summary>A property of the claim, not a promise to consumers (024).</summary>
    [Fact]
    public async Task Dispatch_ShouldClaimInDueOrder()
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

        Assert.True(stored.NextAttemptAt > stored.OccurredAt);
    }

    [Fact]
    public async Task Dispatch_WhenOneMessageFails_ShouldStillDeliverTheRest()
    {
        var poison = Guid.NewGuid();
        var good = Guid.NewGuid();

        await SeedSoldAsync(poison);
        await SeedSoldAsync(good);

        var handler = new RecordingHandler { FailForSeat = poison };
        await using var host = Host(handler);

        Assert.Equal(2, await host.Dispatcher.DispatchBatchAsync(CancellationToken.None));

        var messages = await MessagesAsync();

        Assert.Null(messages[0].ProcessedAt);
        Assert.Equal(1, messages[0].Attempts);
        Assert.NotNull(messages[1].ProcessedAt);
    }

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

        Assert.True(
            started.Elapsed < TimeSpan.FromSeconds(10),
            $"The tick waited {started.Elapsed} on a handler it had given 100ms.");

        var stored = Assert.Single(await MessagesAsync());

        Assert.Null(stored.ProcessedAt);
        Assert.Equal(1, stored.Attempts);
        Assert.Empty(handler.Delivered);
    }

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

        // Claimed three, delivered one: the first handler used the whole budget.
        Assert.Equal(3, await host.Dispatcher.DispatchBatchAsync(CancellationToken.None));

        var afterFirst = await MessagesAsync();

        Assert.Single(afterFirst, message => message.ProcessedAt is not null);
        Assert.Equal(2, afterFirst.Count(message => message.ProcessedAt is null && message.Attempts == 0));

        await using var patient = Host(new RecordingHandler());

        Assert.Equal(2, await patient.Dispatcher.DispatchBatchAsync(CancellationToken.None));
        Assert.All(await MessagesAsync(), message => Assert.NotNull(message.ProcessedAt));
    }

    [Fact]
    public async Task Dispatch_ShouldDeliverEachMessageInItsOwnScope()
    {
        await SeedSoldAsync(Guid.NewGuid());
        await SeedSoldAsync(Guid.NewGuid());

        var seen = new ConcurrentQueue<ScopedHandler>();

        await using var host = Host(services =>
        {
            services.AddSingleton(seen);
            services.AddScoped<IIntegrationEventHandler<SeatSoldV1>, ScopedHandler>();
        });

        Assert.Equal(2, await host.Dispatcher.DispatchBatchAsync(CancellationToken.None));
        Assert.Equal(2, seen.Distinct().Count());
    }

    [Fact]
    public async Task Dispatch_WhenAMessageExhaustsItsAttempts_ShouldStopClaimingIt()
    {
        await SeedSoldAsync(Guid.NewGuid());

        var handler = new RecordingHandler { Fail = true };

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

        Assert.Equal(0, await host.Dispatcher.DispatchBatchAsync(CancellationToken.None));

        var stored = Assert.Single(await MessagesAsync());

        Assert.Equal(2, stored.Attempts);
        Assert.Null(stored.ProcessedAt);
        Assert.Equal(2, handler.Delivered.Count + handler.Failures);
    }

    [Fact]
    public async Task Dispatch_WhenTwoDispatchersRunTogether_ShouldDeliverEachMessageOnce()
    {
        const int Messages = 20;

        for (var i = 0; i < Messages; i++)
        {
            await SeedSoldAsync(Guid.NewGuid());
        }

        var handler = new RecordingHandler();

        // Half a batch each, so neither takes everything first.
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

        var seatIds = handler.Delivered.Select(delivery => delivery.SeatId).ToList();

        Assert.Equal(seatIds.Count, seatIds.Distinct().Count());
        Assert.Equal(claimed.Sum(), seatIds.Count);

        var processed = (await MessagesAsync()).Count(message => message.ProcessedAt is not null);
        Assert.Equal(seatIds.Count, processed);
    }

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

        await using var context = new InventoryDbContext(_database.Options);

        context.OutboxMessages.Add(message);
        await context.SaveChangesAsync();

        return message.MessageId;
    }

    private async Task<List<OutboxMessage>> MessagesAsync()
    {
        await using var context = new InventoryDbContext(_database.Options);

        return await context.OutboxMessages.AsNoTracking()
            .OrderBy(message => message.Id)
            .ToListAsync();
    }

    private DispatcherHost Host(
        IIntegrationEventHandler<SeatSoldV1> handler,
        Action<OutboxOptions>? configure = null) =>
        Host(services => services.AddSingleton(handler), configure);

    private DispatcherHost Host(
        Action<IServiceCollection> registerHandlers,
        Action<OutboxOptions>? configure = null)
    {
        var options = new OutboxOptions();
        configure?.Invoke(options);

        var services = new ServiceCollection();

        services.AddDbContext<InventoryDbContext>(builder =>
            builder.UseInventoryNpgsql(_database.ConnectionString));

        registerHandlers(services);

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

    private sealed class ScopedHandler(ConcurrentQueue<ScopedHandler> seen) : IIntegrationEventHandler<SeatSoldV1>
    {
        public Task HandleAsync(SeatSoldV1 integrationEvent, Guid messageId, CancellationToken cancellationToken)
        {
            seen.Enqueue(this);
            return Task.CompletedTask;
        }
    }

    // Thread-safe: the two-dispatcher test shares one instance.
    private sealed class RecordingHandler : IIntegrationEventHandler<SeatSoldV1>
    {
        private readonly ConcurrentQueue<Delivery> _delivered = new();
        private int _failures;

        internal bool Fail { get; init; }

        internal Guid? FailForSeat { get; init; }

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
                // Honour the token, as real work would; the deadline arrives as a cancellation.
                await Task.Delay(Stall, cancellationToken);
            }

            if (Fail || FailForSeat == integrationEvent.SeatId)
            {
                Interlocked.Increment(ref _failures);

                throw new InvalidOperationException("handler refused");
            }

            _delivered.Enqueue(new Delivery(messageId, integrationEvent));
        }

        internal sealed record Delivery(Guid MessageId, SeatSoldV1 Event)
        {
            internal Guid SeatId => Event.SeatId;
        }
    }
}
