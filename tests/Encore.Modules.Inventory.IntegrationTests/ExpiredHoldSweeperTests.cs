using System.Text.Json;
using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Adapters.Scheduling;
using Encore.Modules.Inventory.Contracts.Events;
using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>
/// The expired-hold sweep against real Postgres: what it tidies, what it leaves alone, and
/// what happens when a row moves underneath it. One sweep is driven directly per test.
/// </summary>
public sealed class ExpiredHoldSweeperTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("encore")
        .WithUsername("encore")
        .WithPassword("encore")
        .Build();

    private readonly Guid _eventId = Guid.NewGuid();
    private readonly Guid _clientA = Guid.NewGuid();
    private readonly Guid _clientB = Guid.NewGuid();

    /// <summary>"Now" for every test, truncated to microseconds to survive the Postgres round trip.</summary>
    private readonly DateTime _now = new(DateTime.UtcNow.Ticks / 10 * 10, DateTimeKind.Utc);

    private DbContextOptions<InventoryDbContext> _options = null!;

    private DateTime LapsedAt => _now - Seat.HoldDuration - TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        _options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInventoryNpgsql(_postgres.GetConnectionString())
            .Options;

        await using var context = new InventoryDbContext(_options);
        await context.Database.MigrateAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task Sweep_WhenAHoldHasLapsed_ShouldReturnTheSeatToAvailable()
    {
        var seatId = await SeedHeldAsync(_clientA, LapsedAt);

        await using var host = Host();
        Assert.Equal(1, await host.Sweeper.SweepBatchAsync(CancellationToken.None));

        var seat = await LoadAsync(seatId);

        Assert.Equal(SeatStatus.Available, seat.Status);

        // The lapsed hold stays on record, so the sweep cannot change what a sale answers.
        Assert.Equal(_clientA, seat.HeldByClientId);
        Assert.Equal(LapsedAt + Seat.HoldDuration, seat.HoldExpiresAt);
    }

    /// <summary>
    /// After the sweep, a sale is refused as it was before it: the lapsed holder hears its hold
    /// expired, and anyone else that they never held it. The sweep is cleanup (006).
    /// </summary>
    [Fact]
    public async Task Sweep_ThenASale_ShouldBeRefusedForTheSameReasonAsBeforeIt()
    {
        var seatId = await SeedHeldAsync(_clientA, LapsedAt);

        await using var host = Host();
        await host.Sweeper.SweepBatchAsync(CancellationToken.None);

        var swept = await LoadAsync(seatId);

        var holder = Assert.Throws<SeatTransitionException>(() => swept.Sell(_clientA, _now));
        var stranger = Assert.Throws<SeatTransitionException>(() => swept.Sell(_clientB, _now));

        Assert.Equal(SeatTransitionReason.HoldExpired, holder.Reason);
        Assert.Equal(SeatTransitionReason.NotTheHolder, stranger.Reason);
    }

    /// <summary>The sweep publishes SeatReleased(Expired), which a bulk UPDATE would have lost.</summary>
    [Fact]
    public async Task Sweep_WhenAHoldHasLapsed_ShouldWriteSeatReleasedToTheOutbox()
    {
        await SeedHeldAsync(_clientA, LapsedAt);

        await using var host = Host();
        await host.Sweeper.SweepBatchAsync(CancellationToken.None);

        var message = Assert.Single(await OutboxAsync());

        Assert.Equal(InventoryEventTypes.SeatReleased, message.EventType);

        // Deserialised: jsonb does not preserve whitespace or key order.
        var released = JsonSerializer.Deserialize<SeatReleasedV1>(
            message.Payload,
            SeatEventPublication.SerializerOptions);

        Assert.NotNull(released);
        Assert.Equal(SeatReleasedV1.Expired, released.Reason);
        Assert.Equal(_clientA, released.ClientId);
        Assert.Equal(_now, released.OccurredAt);
    }

    /// <summary>A live hold is never expired.</summary>
    [Fact]
    public async Task Sweep_WhenAHoldIsStillLive_ShouldLeaveItAlone()
    {
        var seatId = await SeedHeldAsync(_clientA, _now - TimeSpan.FromMinutes(1));

        await using var host = Host();
        Assert.Equal(0, await host.Sweeper.SweepBatchAsync(CancellationToken.None));

        var seat = await LoadAsync(seatId);

        Assert.Equal(SeatStatus.Held, seat.Status);
        Assert.Equal(_clientA, seat.HeldByClientId);
        Assert.Empty(await OutboxAsync());
    }

    [Fact]
    public async Task Sweep_WhenASeatIsSold_ShouldLeaveItAlone()
    {
        var seatId = await SeedSoldAsync(_clientA);

        await using var host = Host();
        await host.Sweeper.SweepBatchAsync(CancellationToken.None);

        var seat = await LoadAsync(seatId);

        Assert.Equal(SeatStatus.Sold, seat.Status);
        Assert.Equal(_clientA, seat.HeldByClientId);
        Assert.Empty(await OutboxAsync());
    }

    /// <summary>
    /// A seat re-held between the candidate query and the write keeps its new hold: the
    /// aggregate re-decides.
    /// </summary>
    [Fact]
    public async Task Sweep_WhenTheSeatIsReHeldFirst_ShouldLeaveTheNewHoldStanding()
    {
        var seatId = await SeedHeldAsync(_clientA, LapsedAt);

        await using var host = Host();

        // A lazy reclaim between the sweep's query and its write.
        await ReHoldAsync(seatId, _clientB);

        await host.Sweeper.SweepBatchAsync(CancellationToken.None);

        var seat = await LoadAsync(seatId);

        Assert.Equal(SeatStatus.Held, seat.Status);
        Assert.Equal(_clientB, seat.HeldByClientId);
    }

    /// <summary>Two sweeps over one table expire each seat once; xmin arbitrates.</summary>
    [Fact]
    public async Task Sweep_WhenTwoSweepsRunTogether_ShouldExpireEachSeatOnce()
    {
        const int Seats = 20;

        for (var i = 0; i < Seats; i++)
        {
            await SeedHeldAsync(Guid.NewGuid(), LapsedAt);
        }

        await using var left = Host();
        await using var right = Host();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runs = new[]
        {
            Task.Run(async () =>
            {
                await gate.Task;
                return await left.Sweeper.SweepBatchAsync(CancellationToken.None);
            }),
            Task.Run(async () =>
            {
                await gate.Task;
                return await right.Sweeper.SweepBatchAsync(CancellationToken.None);
            })
        };

        gate.SetResult();
        await Task.WhenAll(runs);

        await using var context = new InventoryDbContext(_options);

        Assert.Equal(
            Seats,
            await context.Seats.CountAsync(seat => seat.Status == SeatStatus.Available));

        // One announcement per seat, not two.
        Assert.Equal(Seats, (await OutboxAsync()).Count);
    }

    [Fact]
    public async Task Sweep_ShouldNotVisitMoreThanItsBatchSize()
    {
        for (var i = 0; i < 5; i++)
        {
            await SeedHeldAsync(Guid.NewGuid(), LapsedAt);
        }

        await using var host = Host(options => options.BatchSize = 2);

        Assert.Equal(2, await host.Sweeper.SweepBatchAsync(CancellationToken.None));

        await using var context = new InventoryDbContext(_options);
        Assert.Equal(3, await context.Seats.CountAsync(seat => seat.Status == SeatStatus.Held));
    }

    [Fact]
    public async Task Sweep_WhenNothingHasLapsed_ShouldDoNothing()
    {
        await SeedHeldAsync(_clientA, _now - TimeSpan.FromMinutes(1));

        await using var host = Host();

        Assert.Equal(0, await host.Sweeper.SweepBatchAsync(CancellationToken.None));
        Assert.Empty(await OutboxAsync());
    }

    /// <summary>Sweeping twice announces once.</summary>
    [Fact]
    public async Task Sweep_WhenRunTwice_ShouldAnnounceOnce()
    {
        await SeedHeldAsync(_clientA, LapsedAt);

        await using var host = Host();

        await host.Sweeper.SweepBatchAsync(CancellationToken.None);
        Assert.Equal(0, await host.Sweeper.SweepBatchAsync(CancellationToken.None));

        Assert.Single(await OutboxAsync());
    }

    /// <summary>
    /// Writes a seat held since <paramref name="heldAt"/>, through <c>Seat.Hold</c>, then clears
    /// its events so only the sweep's rows are asserted.
    /// </summary>
    private async Task<Guid> SeedHeldAsync(Guid clientId, DateTime heldAt)
    {
        var seatId = Guid.NewGuid();
        var seat = Seat.Create(seatId, _eventId);

        seat.Hold(clientId, heldAt);
        seat.ClearDomainEvents();

        await using var context = new InventoryDbContext(_options);
        context.Seats.Add(seat);
        await context.SaveChangesAsync();

        return seatId;
    }

    private async Task<Guid> SeedSoldAsync(Guid clientId)
    {
        var seatId = Guid.NewGuid();
        var seat = Seat.Create(seatId, _eventId);

        seat.Hold(clientId, _now - TimeSpan.FromMinutes(1));
        seat.Sell(clientId, _now);
        seat.ClearDomainEvents();

        await using var context = new InventoryDbContext(_options);
        context.Seats.Add(seat);
        await context.SaveChangesAsync();

        return seatId;
    }

    private async Task ReHoldAsync(Guid seatId, Guid clientId)
    {
        await using var context = new InventoryDbContext(_options);
        var repository = new EfSeatRepository(context);

        var seat = await repository.GetByIdAsync(seatId);
        seat!.Hold(clientId, _now);

        await repository.SaveAsync(seat);
    }

    private async Task<Seat> LoadAsync(Guid seatId)
    {
        await using var context = new InventoryDbContext(_options);

        return await context.Seats.AsNoTracking().SingleAsync(seat => seat.Id == seatId);
    }

    private async Task<List<OutboxMessage>> OutboxAsync()
    {
        await using var context = new InventoryDbContext(_options);

        return await context.OutboxMessages.AsNoTracking()
            .OrderBy(message => message.Id)
            .ToListAsync();
    }

    /// <summary>A container resolving <c>ISeatRepository</c> per scope, with a fixed clock.</summary>
    private SweeperHost Host(Action<ExpiredHoldSweepOptions>? configure = null)
    {
        var options = new ExpiredHoldSweepOptions();
        configure?.Invoke(options);

        var services = new ServiceCollection();

        services.AddDbContext<InventoryDbContext>(builder =>
            builder.UseInventoryNpgsql(_postgres.GetConnectionString()));

        services.AddScoped<ISeatRepository, EfSeatRepository>();

        var provider = services.BuildServiceProvider();

        var sweeper = new ExpiredHoldSweeper(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            new FixedTimeProvider(_now),
            NullLogger<ExpiredHoldSweeper>.Instance);

        return new SweeperHost(provider, sweeper);
    }

    private sealed class SweeperHost(ServiceProvider provider, ExpiredHoldSweeper sweeper)
        : IAsyncDisposable
    {
        internal ExpiredHoldSweeper Sweeper { get; } = sweeper;

        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }

    /// <summary>A clock that does not move, so expiry is data rather than duration.</summary>
    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
