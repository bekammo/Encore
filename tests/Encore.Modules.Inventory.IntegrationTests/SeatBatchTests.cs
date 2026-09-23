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
/// An order's seats written together against real Postgres: a sale of every seat or none, and
/// holds and releases answered per seat but written in one transaction. Seats written together
/// share one <c>xmin</c>, which is how "one transaction" is checked.
/// </summary>
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

    /// <summary>Four seats, the last hold lapsed: none sell, nothing is announced, the live holds remain.</summary>
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
    /// After a refused sale, a later save on the same context sells nothing: the seats that read
    /// Sold in memory were reloaded.
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
    /// Another writer moves a seat before each of the two saves. Both attempts lose, and a later
    /// save on the same context writes nothing and does not fail on the stale seats.
    /// </summary>
    [Fact]
    public async Task Sell_WhenBothAttemptsLoseTheRace_ShouldLeaveNothingForALaterSaveToWrite()
    {
        var clientId = Guid.NewGuid();
        var seatIds = await SeedHeldAsync(clientId, _now, count: 2);

        await using (var context = new InventoryDbContext(_options))
        {
            var contested = new MovedBeforeEachSave(
                new EfSeatRepository(context),
                () => MoveAsync(seatIds[1], clientId));

            var result = await new SellSeatCommandHandler(contested, new FixedTimeProvider(_now))
                .HandleAsync(new SellSeatsCommand(_eventId, seatIds, clientId));

            Assert.Equal([new SeatSaleRefusal(seatIds[1], SellSeatOutcome.LostRace)], result.Refusals);

            await context.SaveChangesAsync();
        }

        Assert.All(await LoadAsync(seatIds), seat => Assert.Equal(SeatStatus.Held, seat.Status));
        Assert.Equal(0, await CountEventsAsync(seatIds, InventoryEventTypes.SeatSold));
    }

    /// <summary>
    /// Another writer moves one seat between load and save; the untouched seat does not sell either.
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

    /// <summary>A retried confirm after the sale: success, and nothing announced twice.</summary>
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

    /// <summary>A seat someone else holds is refused; the free ones are held in one write.</summary>
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

    /// <summary>The cap counts across the batch: the database ends with exactly the cap.</summary>
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

    /// <summary>
    /// Another writer moves a seat before each save, so both hold attempts lose. A later save
    /// on the same context writes no hold and does not fail on the stale seats.
    /// </summary>
    [Fact]
    public async Task Hold_WhenBothAttemptsLoseTheRace_ShouldLeaveNothingForALaterSaveToWrite()
    {
        var clientId = Guid.NewGuid();
        var seatIds = await SeedAvailableAsync(count: 2);

        await using (var context = new InventoryDbContext(_options))
        {
            var contested = new MovedBeforeEachSave(
                new EfSeatRepository(context),
                () => BumpAsync(seatIds[1]));

            var results = await new HoldSeatCommandHandler(contested, new AlwaysGrantingLock(), new FixedTimeProvider(_now))
                .HandleAsync(new HoldSeatsCommand(_eventId, seatIds, clientId));

            Assert.All(results, result => Assert.Equal(HoldSeatOutcome.LostRace, result.Outcome));

            await context.SaveChangesAsync();
        }

        Assert.All(await LoadAsync(seatIds), seat => Assert.Equal(SeatStatus.Available, seat.Status));
        Assert.Equal(0, await CountEventsAsync(seatIds, InventoryEventTypes.SeatHeld));
    }

    /// <summary>
    /// A hold reads the seats it asks for and the client's other live holds in one query. The
    /// other holds are counted for the cap and nothing more: not tracked, so no save can write them.
    /// </summary>
    [Fact]
    public async Task Hold_ShouldCountTheClientsOtherHoldsWithoutTakingThemIn()
    {
        var clientId = Guid.NewGuid();
        var others = await SeedHeldAsync(clientId, _now, count: 2);
        var lapsed = await SeedHeldAsync(clientId, _now - Seat.HoldDuration - TimeSpan.FromMinutes(1), count: 1);
        var asked = await SeedAvailableAsync(count: 1);

        await using var context = new InventoryDbContext(_options);
        var repository = new EfSeatRepository(context);

        var loaded = await repository.GetForHoldAsync(asked, clientId, _eventId, _now);

        Assert.Equal(asked, loaded.Seats.Select(seat => seat.Id));
        Assert.Equal(others.Order(), loaded.LiveHolds.Order());
        Assert.DoesNotContain(lapsed[0], loaded.LiveHolds);

        Assert.Equal(asked, context.ChangeTracker.Entries<Seat>().Select(entry => entry.Entity.Id));
    }

    // -- Releasing ----------------------------------------------------------

    /// <summary>
    /// Both release attempts lose to another writer. A later save on the same context releases
    /// nothing and does not fail on the stale seats.
    /// </summary>
    [Fact]
    public async Task Release_WhenBothAttemptsLoseTheRace_ShouldLeaveNothingForALaterSaveToWrite()
    {
        var clientId = Guid.NewGuid();
        var seatIds = await SeedHeldAsync(clientId, _now, count: 2);

        await using (var context = new InventoryDbContext(_options))
        {
            var contested = new MovedBeforeEachSave(
                new EfSeatRepository(context),
                () => MoveAsync(seatIds[1], clientId));

            var results = await new ReleaseSeatCommandHandler(contested, new FixedTimeProvider(_now))
                .HandleAsync(new ReleaseSeatsCommand(_eventId, seatIds, clientId));

            Assert.All(results, result => Assert.Equal(ReleaseSeatOutcome.LostRace, result));

            await context.SaveChangesAsync();
        }

        Assert.All(await LoadAsync(seatIds), seat =>
        {
            Assert.Equal(SeatStatus.Held, seat.Status);
            Assert.Equal(clientId, seat.HeldByClientId);
        });
    }

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

            Assert.All(results, result => Assert.Equal(ReleaseSeatOutcome.Released, result));
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
    /// Moves a seat's row version through another context without changing its holder: release
    /// and re-hold. One write would not do, since EF sends no UPDATE when nothing changed.
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

    /// <summary>
    /// Moves an available seat's row version through another context and leaves it available:
    /// a stranger holds it, then releases it. Its events are dropped, so only the handler's count.
    /// </summary>
    private async Task BumpAsync(Guid seatId)
    {
        var stranger = Guid.NewGuid();

        await using var context = new InventoryDbContext(_options);
        var repository = new EfSeatRepository(context);

        var seat = await repository.GetByIdAsync(seatId);

        seat!.Hold(stranger, _now);
        seat.ClearDomainEvents();
        await repository.SaveAsync(seat);

        seat.Release(stranger, _now);
        seat.ClearDomainEvents();
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

        // One query per seat, filtered on the payload, because the container is shared.
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

    /// <summary>A lock that grants everything; nothing here needs serialising.</summary>
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

    /// <summary>A real repository whose batch saves each lose a race, because another writer moves first.</summary>
    private sealed class MovedBeforeEachSave(ISeatRepository inner, Func<Task> move) : ISeatRepository
    {
        public Task<Seat?> GetByIdAsync(Guid seatId, CancellationToken cancellationToken = default) =>
            inner.GetByIdAsync(seatId, cancellationToken);

        public Task<IReadOnlyList<Seat>> GetByIdsAsync(
            IReadOnlyCollection<Guid> seatIds,
            CancellationToken cancellationToken = default) =>
            inner.GetByIdsAsync(seatIds, cancellationToken);

        public Task SaveAsync(Seat seat, CancellationToken cancellationToken = default) =>
            inner.SaveAsync(seat, cancellationToken);

        public async Task SaveAsync(IReadOnlyCollection<Seat> seats, CancellationToken cancellationToken = default)
        {
            await move();
            await inner.SaveAsync(seats, cancellationToken);
        }

        public Task<SeatsForHold> GetForHoldAsync(
            IReadOnlyCollection<Guid> seatIds,
            Guid clientId,
            Guid eventId,
            DateTime utcNow,
            CancellationToken cancellationToken = default) =>
            inner.GetForHoldAsync(seatIds, clientId, eventId, utcNow, cancellationToken);

        public Task<IReadOnlyList<Guid>> FindExpiredHoldsAsync(
            DateTime utcNow,
            int limit,
            CancellationToken cancellationToken = default) =>
            inner.FindExpiredHoldsAsync(utcNow, limit, cancellationToken);

        public Task AddRangeAsync(IReadOnlyCollection<Seat> seats, CancellationToken cancellationToken = default) =>
            inner.AddRangeAsync(seats, cancellationToken);
    }
}
