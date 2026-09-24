using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Application;
using Encore.Modules.Inventory.Contracts;
using Encore.Modules.Inventory.Contracts.Events;
using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>
/// A sale writes every seat or none; holds and releases answer per seat but write in one
/// transaction (011). Seats written together share one <c>xmin</c>, which is how "one
/// transaction" is checked. The database is never emptied, so assertions are scoped to the
/// test's own seats.
/// </summary>
public sealed class SeatBatchTests(InventoryDatabase database) : IClassFixture<InventoryDatabase>
{
    private readonly Guid _eventId = Guid.NewGuid();
    private readonly DateTime _now = Now();

    private readonly DbContextOptions<InventoryDbContext> _options = database.Options;

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

    /// <summary>The later save must neither write the stale seats nor fail on them.</summary>
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

            var result = await new SellSeatCommandHandler(contested, new FakeTimeProvider(_now))
                .HandleAsync(new SellSeatsCommand(_eventId, seatIds, clientId));

            Assert.Equal([new SeatSaleRefusal(seatIds[1], SellSeatOutcome.LostRace)], result.Refusals);

            await context.SaveChangesAsync();
        }

        Assert.All(await LoadAsync(seatIds), seat => Assert.Equal(SeatStatus.Held, seat.Status));
        Assert.Equal(0, await CountEventsAsync(seatIds, InventoryEventTypes.SeatSold));
    }

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

    /// <summary>The cap counts the holds this batch has already made.</summary>
    [Fact]
    public async Task Hold_WhenTheCapRunsOutPartWay_ShouldStopAtTheCap()
    {
        var clientId = Guid.NewGuid();
        await SeedHeldAsync(clientId, _now, count: 2);
        var asked = await SeedAvailableAsync(count: 4);

        var results = await HoldAsync(asked, clientId);

        Assert.Equal(
            [
                HoldSeatOutcome.Held,
                HoldSeatOutcome.Held,
                HoldSeatOutcome.HoldCapReached,
                HoldSeatOutcome.HoldCapReached
            ],
            results.Select(result => result.Outcome));

        await using var context = new InventoryDbContext(_options);
        Assert.Equal(
            SeatReservationLimits.MaxHoldsPerClientPerEvent,
            await context.Seats.CountAsync(seat => seat.HeldByClientId == clientId && seat.Status == SeatStatus.Held));
    }

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

            var results = await new HoldSeatCommandHandler(
                    contested,
                    new AlwaysGrantingLock(),
                    new FakeTimeProvider(_now))
                .HandleAsync(new HoldSeatsCommand(_eventId, seatIds, clientId));

            Assert.All(results, result => Assert.Equal(HoldSeatOutcome.LostRace, result.Outcome));

            await context.SaveChangesAsync();
        }

        Assert.All(await LoadAsync(seatIds), seat => Assert.Equal(SeatStatus.Available, seat.Status));
        Assert.Equal(0, await CountEventsAsync(seatIds, InventoryEventTypes.SeatHeld));
    }

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

            var results = await new ReleaseSeatCommandHandler(contested, new FakeTimeProvider(_now))
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
                    new FakeTimeProvider(_now))
                .HandleAsync(new ReleaseSeatsCommand(_eventId, seatIds, clientId));

            Assert.All(results, result => Assert.Equal(ReleaseSeatOutcome.Released, result));
        }

        var seats = await LoadAsync(seatIds);
        Assert.All(seats, seat => Assert.Equal(SeatStatus.Available, seat.Status));
        Assert.Single(seats.Select(seat => seat.RowVersion).Distinct());
    }

    private SellSeatCommandHandler SellHandler(InventoryDbContext context) =>
        new(new EfSeatRepository(context), new FakeTimeProvider(_now));

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
            new FakeTimeProvider(_now));

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

    // Two saves, since EF sends no UPDATE when nothing changed.
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

    // Events dropped so only the handler's are counted.
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

        foreach (var seatId in seatIds)
        {
            count += await context.OutboxMessages.AsNoTracking()
                .CountAsync(message =>
                    message.EventType == eventType
                    && EF.Functions.JsonContains(message.Payload, $$"""{"seatId":"{{seatId}}"}"""));
        }

        return count;
    }

    // Truncated to microseconds to survive the Postgres round trip.
    private static DateTime Now()
    {
        var utcNow = DateTime.UtcNow;

        return new DateTime(utcNow.Ticks - utcNow.Ticks % TimeSpan.TicksPerMicrosecond, DateTimeKind.Utc);
    }

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
