using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Application;
using Encore.Modules.Inventory.Contracts.Events;
using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>
/// An order's seats written together (076): a sale of every seat or none, and
/// holds and releases that answer each seat but land in one transaction.
/// </summary>
/// <remarks>
/// <para>
/// Real Postgres, because the claim is about what a transaction does and a fake
/// repository can only be told what to pretend. The unit tests pin the handlers'
/// sequencing; these pin that the database agrees with them.
/// </para>
/// <para>
/// "One transaction" is checked, not assumed. Postgres stamps every row a
/// transaction writes with that transaction's id in <c>xmin</c>, which is what
/// <see cref="Seat.RowVersion"/> maps, so seats written together share one.
/// </para>
/// </remarks>
public sealed class SeatBatchTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("encore")
        .WithUsername("encore")
        .WithPassword("encore")
        .Build();

    private readonly Guid _eventId = Guid.NewGuid();
    private readonly DateTime _now = Now();

    private DbContextOptions<InventoryDbContext> _options = null!;

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

    // -- Selling: all or none ----------------------------------------------

    [Fact]
    public async Task Sell_WhenEveryHoldIsLive_ShouldSellThemAllInOneTransaction()
    {
        var clientId = Guid.NewGuid();
        var seatIds = await SeedHeldAsync(clientId, _now, count: 4);

        var result = await SellAsync(seatIds, clientId);

        Assert.True(result.AllSold);

        var seats = await LoadAsync(seatIds);
        Assert.All(seats, seat => Assert.Equal(SeatStatus.Sold, seat.Status));
        Assert.Single(seats.Select(seat => seat.RowVersion).Distinct());

        Assert.Equal(4, await CountEventsAsync(seatIds, InventoryEventTypes.SeatSold));
    }

    /// <summary>
    /// The case 028 had to leave for a person: four seats, the last hold lapsed.
    /// Sold one at a time, three stayed sold with nobody paying for them. Now none
    /// sell, nothing is announced, and the three live holds are still the client's.
    /// </summary>
    [Fact]
    public async Task Sell_WhenOneOfFourHoldsHasLapsed_ShouldSellNone()
    {
        var clientId = Guid.NewGuid();
        var live = await SeedHeldAsync(clientId, _now, count: 3);
        var lapsed = await SeedHeldAsync(clientId, _now - Seat.HoldDuration - TimeSpan.FromMinutes(1), count: 1);
        var seatIds = live.Concat(lapsed).ToList();

        var result = await SellAsync(seatIds, clientId);

        Assert.Equal([new SeatSaleRefusal(lapsed[0], SellSeatOutcome.HoldExpired)], result.Refusals);

        var seats = await LoadAsync(live);
        Assert.All(seats, seat =>
        {
            Assert.Equal(SeatStatus.Held, seat.Status);
            Assert.Equal(clientId, seat.HeldByClientId);
        });

        Assert.Equal(0, await CountEventsAsync(seatIds, InventoryEventTypes.SeatSold));
    }

    /// <summary>
    /// The hazard the refused path has to close. The seats that would have sold
    /// already read Sold in memory when the lapsed one refused; if they stayed
    /// that way, the next save on the same unit of work — any save, for any
    /// reason — would sell them. The handler reads them again so it cannot.
    /// </summary>
    [Fact]
    public async Task Sell_WhenRefused_ShouldLeaveNothingForALaterSaveToWrite()
    {
        var clientId = Guid.NewGuid();
        var live = await SeedHeldAsync(clientId, _now, count: 2);
        var lapsed = await SeedHeldAsync(clientId, _now - Seat.HoldDuration - TimeSpan.FromMinutes(1), count: 1);

        await using (var context = new InventoryDbContext(_options))
        {
            var result = await SellHandler(context)
                .HandleAsync(new SellSeatsCommand(_eventId, [.. live, .. lapsed], clientId));

            Assert.False(result.AllSold);

            await context.SaveChangesAsync();
        }

        Assert.All(await LoadAsync(live), seat => Assert.Equal(SeatStatus.Held, seat.Status));
        Assert.Equal(0, await CountEventsAsync(live, InventoryEventTypes.SeatSold));
    }

    /// <summary>
    /// The transaction itself, beneath the handler: another writer moves one seat
    /// between the load and the save, and the seat that was not touched does not
    /// sell either. This is the property the whole of 076 rests on.
    /// </summary>
    [Fact]
    public async Task Save_WhenAnotherWriterMovesOneSeat_ShouldCommitNone()
    {
        var clientId = Guid.NewGuid();
        var seatIds = await SeedHeldAsync(clientId, _now, count: 2);

        await using var context = new InventoryDbContext(_options);
        var repository = new EfSeatRepository(context);

        var seats = await repository.GetByIdsAsync(seatIds);

        foreach (var seat in seats)
        {
            seat.Sell(clientId, _now);
        }

        await MoveAsync(seatIds[1], clientId);

        await Assert.ThrowsAsync<ConcurrentSeatModificationException>(() => repository.SaveAsync(seats));

        Assert.Equal(SeatStatus.Held, (await LoadAsync([seatIds[0]])).Single().Status);
        Assert.Equal(0, await CountEventsAsync(seatIds, InventoryEventTypes.SeatSold));
    }

    /// <summary>
    /// A confirm retried after the sale went through: every seat is already the
    /// client's, which is success, and nothing is announced twice.
    /// </summary>
    [Fact]
    public async Task Sell_WhenRetriedAfterTheSale_ShouldSucceedWithoutASecondEvent()
    {
        var clientId = Guid.NewGuid();
        var seatIds = await SeedHeldAsync(clientId, _now, count: 2);

        await SellAsync(seatIds, clientId);
        var retried = await SellAsync(seatIds, clientId);

        Assert.True(retried.AllSold);
        Assert.Equal(2, await CountEventsAsync(seatIds, InventoryEventTypes.SeatSold));
    }

    // -- Holding: every seat answered, one transaction ----------------------

    /// <summary>
    /// 023 survives the batch: the seat somebody else holds is refused and the
    /// two free ones are held — together, in one write.
    /// </summary>
    [Fact]
    public async Task Hold_WhenOneSeatIsTaken_ShouldHoldTheOthersInOneTransaction()
    {
        var clientId = Guid.NewGuid();
        var free = await SeedAvailableAsync(count: 2);
        var taken = await SeedHeldAsync(Guid.NewGuid(), _now, count: 1);

        var results = await HoldAsync([free[0], taken[0], free[1]], clientId);

        Assert.Equal(
            [HoldSeatOutcome.Held, HoldSeatOutcome.AlreadyHeld, HoldSeatOutcome.Held],
            results.Select(result => result.Outcome));

        var held = await LoadAsync(free);
        Assert.All(held, seat => Assert.Equal(clientId, seat.HeldByClientId));
        Assert.Single(held.Select(seat => seat.RowVersion).Distinct());

        Assert.Equal(2, await CountEventsAsync(free, InventoryEventTypes.SeatHeld));
    }

    /// <summary>
    /// The cap counts across the batch as it did across separate calls: two
    /// holds elsewhere leave room for two of the four asked for, and the
    /// database ends with exactly the cap.
    /// </summary>
    [Fact]
    public async Task Hold_WhenTheCapRunsOutPartWay_ShouldStopAtTheCap()
    {
        var clientId = Guid.NewGuid();
        await SeedHeldAsync(clientId, _now, count: 2);
        var asked = await SeedAvailableAsync(count: 4);

        var results = await HoldAsync(asked, clientId);

        Assert.Equal(
            [HoldSeatOutcome.Held, HoldSeatOutcome.Held, HoldSeatOutcome.HoldCapReached, HoldSeatOutcome.HoldCapReached],
            results.Select(result => result.Outcome));

        await using var context = new InventoryDbContext(_options);
        Assert.Equal(
            HoldSeatCommandHandler.MaxHoldsPerClientPerEvent,
            await context.Seats.CountAsync(seat => seat.HeldByClientId == clientId && seat.Status == SeatStatus.Held));
    }

    // -- Releasing ----------------------------------------------------------

    [Fact]
    public async Task Release_ShouldGiveEverySeatBackInOneTransaction()
    {
        var clientId = Guid.NewGuid();
        var seatIds = await SeedHeldAsync(clientId, _now, count: 3);

        await using (var context = new InventoryDbContext(_options))
        {
            var results = await new ReleaseSeatCommandHandler(
                    new EfSeatRepository(context),
                    new FixedTimeProvider(_now))
                .HandleAsync(new ReleaseSeatsCommand(_eventId, seatIds, clientId));

            Assert.All(results, result => Assert.Equal(ReleaseSeatOutcome.Released, result.Outcome));
        }

        var seats = await LoadAsync(seatIds);
        Assert.All(seats, seat => Assert.Equal(SeatStatus.Available, seat.Status));
        Assert.Single(seats.Select(seat => seat.RowVersion).Distinct());
    }

    // -- Helpers ------------------------------------------------------------

    private SellSeatCommandHandler SellHandler(InventoryDbContext context) =>
        new(new EfSeatRepository(context), new FixedTimeProvider(_now));

    private async Task<SellSeatsResult> SellAsync(IReadOnlyList<Guid> seatIds, Guid clientId)
    {
        await using var context = new InventoryDbContext(_options);

        return await SellHandler(context).HandleAsync(new SellSeatsCommand(_eventId, seatIds, clientId));
    }

    private async Task<IReadOnlyList<HoldSeatResult>> HoldAsync(IReadOnlyList<Guid> seatIds, Guid clientId)
    {
        await using var context = new InventoryDbContext(_options);

        var handler = new HoldSeatCommandHandler(
            new EfSeatRepository(context),
            new AlwaysGrantingLock(),
            new FixedTimeProvider(_now));

        return await handler.HandleAsync(new HoldSeatsCommand(_eventId, seatIds, clientId));
    }

    private async Task<List<Guid>> SeedHeldAsync(Guid clientId, DateTime heldAt, int count)
    {
        var seats = Enumerable.Range(0, count).Select(_ => Seat.Create(Guid.NewGuid(), _eventId)).ToList();

        foreach (var seat in seats)
        {
            seat.Hold(clientId, heldAt);
            seat.ClearDomainEvents();
        }

        await using var context = new InventoryDbContext(_options);
        context.Seats.AddRange(seats);
        await context.SaveChangesAsync();

        return [.. seats.Select(seat => seat.Id)];
    }

    private async Task<List<Guid>> SeedAvailableAsync(int count)
    {
        var seats = Enumerable.Range(0, count).Select(_ => Seat.Create(Guid.NewGuid(), _eventId)).ToList();

        await using var context = new InventoryDbContext(_options);
        context.Seats.AddRange(seats);
        await context.SaveChangesAsync();

        return [.. seats.Select(seat => seat.Id)];
    }

    /// <summary>
    /// Writes to one seat through another context without changing whose it is:
    /// the client releases it and holds it again, two writes, so its row version
    /// moves while it stays theirs. One write would not do — releasing and
    /// re-holding at the same instant leaves every column as it was, and EF sends
    /// no UPDATE for a row with nothing to change.
    /// </summary>
    private async Task MoveAsync(Guid seatId, Guid clientId)
    {
        await using var context = new InventoryDbContext(_options);
        var repository = new EfSeatRepository(context);

        var seat = await repository.GetByIdAsync(seatId);

        seat!.Release(clientId, _now);
        await repository.SaveAsync(seat);

        seat.Hold(clientId, _now);
        await repository.SaveAsync(seat);
    }

    private async Task<List<Seat>> LoadAsync(IReadOnlyCollection<Guid> seatIds)
    {
        await using var context = new InventoryDbContext(_options);

        return await context.Seats.AsNoTracking().Where(seat => seatIds.Contains(seat.Id)).ToListAsync();
    }

    private async Task<int> CountEventsAsync(IEnumerable<Guid> seatIds, string eventType)
    {
        await using var context = new InventoryDbContext(_options);

        var count = 0;

        // One query per seat, filtered in the payload the way OutboxDrainTests
        // scopes its assertions, because the container is shared across the class.
        foreach (var seatId in seatIds)
        {
            count += await context.OutboxMessages.AsNoTracking()
                .CountAsync(message =>
                    message.EventType == eventType
                    && EF.Functions.JsonContains(message.Payload, $$"""{"seatId":"{{seatId}}"}"""));
        }

        return count;
    }

    private static DateTime Now()
    {
        var utcNow = DateTime.UtcNow;

        return new DateTime(utcNow.Ticks - (utcNow.Ticks % TimeSpan.TicksPerMicrosecond), DateTimeKind.Utc);
    }

    /// <summary>
    /// A lock that grants everything. The hold handler takes the port; nothing
    /// here races the client against itself, so there is nothing to serialise.
    /// </summary>
    private sealed class AlwaysGrantingLock : IDistributedLock
    {
        public Task<LockAcquisition> TryAcquireAsync(
            string resource,
            TimeSpan ttl,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(LockAcquisition.Acquired(Guid.NewGuid().ToString("N")));

        public Task<bool> ReleaseAsync(
            string resource,
            string token,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
