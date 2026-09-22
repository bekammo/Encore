using System.Text.Json;
using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Adapters.Scheduling;
using Encore.Modules.Inventory.Contracts.Events;
using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>
/// The expired-hold sweep against real Postgres: what it tidies, what it leaves
/// alone, and what it does when the row moves underneath it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every test here is about the sweep doing no harm.</b> That is the inverted
/// shape this job deserves — the good it does is cosmetic (a row that reads
/// <c>Available</c> rather than a lapsed <c>Held</c>) while the harm it could do is
/// real, because a sweep that expired a live hold would take a seat away from a
/// customer mid-checkout. <c>ExpiryWithoutTheSweepTests</c> is the other half of
/// the argument: that nothing breaks when this never runs.
/// </para>
/// <para>
/// <b>One sweep is driven directly rather than by starting the hosted service</b>,
/// for <c>OutboxDispatcherTests</c>' reason: a test that started it and waited
/// would be timing-dependent, and about a background job specifically, timing
/// flake is indistinguishable from the bug under test.
/// </para>
/// </remarks>
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

    /// <summary>
    /// The instant every test calls "now", truncated to whole microseconds.
    /// </summary>
    /// <remarks>
    /// Postgres <c>timestamptz</c> resolves to a microsecond and a <c>DateTime</c>
    /// tick is 100ns, so an untruncated instant does not survive the round trip —
    /// and an expiry assertion would then fail on a hold that was perfectly
    /// correct. <c>ConcurrentHoldTests</c> truncates for the same reason.
    /// </remarks>
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
        Assert.Null(seat.HeldByClientId);
        Assert.Null(seat.HoldExpiresAt);
    }

    /// <summary>
    /// The sweep publishes, and that is the half a bulk UPDATE would have lost.
    /// </summary>
    /// <remarks>
    /// A hold nobody ever came back for would otherwise end with no record of it
    /// ending — the log would show a claim and then silence, and 007's promise that
    /// hold history is reconstructable would hold only for seats that happened to
    /// be popular. The row lands through the aggregate and therefore through the
    /// drain, in the same transaction as the state change.
    /// </remarks>
    [Fact]
    public async Task Sweep_WhenAHoldHasLapsed_ShouldWriteSeatReleasedToTheOutbox()
    {
        await SeedHeldAsync(_clientA, LapsedAt);

        await using var host = Host();
        await host.Sweeper.SweepBatchAsync(CancellationToken.None);

        var message = Assert.Single(await OutboxAsync());

        Assert.Equal(InventoryEventTypes.SeatReleased, message.EventType);

        // Deserialised rather than string-matched: the column is jsonb, so
        // Postgres normalises the whitespace and does not promise key order.
        var released = JsonSerializer.Deserialize<SeatReleasedV1>(
            message.Payload,
            SeatEventPublication.SerializerOptions);

        Assert.NotNull(released);
        Assert.Equal(SeatReleasedV1.Expired, released.Reason);
        Assert.Equal(_clientA, released.ClientId);
        Assert.Equal(_now, released.OccurredAt);
    }

    /// <summary>The one that would be a customer losing their seat.</summary>
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
    /// A seat re-held between the candidate query and the write keeps its new
    /// hold, because the aggregate re-decides on the instance the sweep actually
    /// loaded.
    /// </summary>
    /// <remarks>
    /// This is the case that would go wrong if the SQL predicate were treated as
    /// the verdict: the query named this seat, and by the time the sweep reached
    /// it the answer had changed. A bulk UPDATE over the same predicate would
    /// re-evaluate it under Postgres' own row locking and also get this right —
    /// but only because of an isolation-level subtlety, rather than because
    /// anything asked the rules.
    /// </remarks>
    [Fact]
    public async Task Sweep_WhenTheSeatIsReHeldFirst_ShouldLeaveTheNewHoldStanding()
    {
        var seatId = await SeedHeldAsync(_clientA, LapsedAt);

        await using var host = Host();

        // The lazy reclaim, exactly as a request would do it, between the sweep's
        // query and its write.
        await ReHoldAsync(seatId, _clientB);

        await host.Sweeper.SweepBatchAsync(CancellationToken.None);

        var seat = await LoadAsync(seatId);

        Assert.Equal(SeatStatus.Held, seat.Status);
        Assert.Equal(_clientB, seat.HeldByClientId);
    }

    /// <summary>
    /// Two sweeps over one table expire each seat once, with no lease and no lock
    /// between them.
    /// </summary>
    /// <remarks>
    /// <c>xmin</c> is the whole mechanism (062). The loser of each row finds out at
    /// its save, writes nothing and publishes nothing, so the count of
    /// <c>SeatReleased</c> rows is the assertion that matters — a duplicated sweep
    /// showing up as duplicated announcements is the failure this rules out. It is
    /// the contrast with <c>PaymentReconciler</c>, whose duplicate work reaches a
    /// gateway that no token arbitrates (061).
    /// </remarks>
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

        // One announcement per seat, not two. This is the assertion the whole
        // no-lease argument rests on.
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

    /// <summary>
    /// Sweeping twice announces once, which is what makes a restarted or
    /// overlapping job harmless.
    /// </summary>
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
    /// Writes a seat holding a hold taken at <paramref name="heldAt"/>.
    /// </summary>
    /// <remarks>
    /// Built through <c>Seat.Hold</c> rather than by setting columns, because 005
    /// leaves no other way in — and then the events are cleared so that the outbox
    /// assertions above see only what the sweep itself wrote.
    /// </remarks>
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

    /// <summary>
    /// A container holding just enough to resolve <c>ISeatRepository</c> per scope,
    /// which is all the sweeper asks of the world.
    /// </summary>
    /// <remarks>
    /// The clock is fixed at <see cref="_now"/> so that "lapsed" is a property of
    /// the seeded data rather than of how long the test took to run.
    /// </remarks>
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
