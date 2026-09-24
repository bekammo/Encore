using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Application;
using Encore.Modules.Inventory.Contracts;
using Encore.Modules.Inventory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>
/// The falsification of lazy expiry (006): no sweep and no Redis, so lapsed rows stay
/// <c>Held</c> throughout. If any test here needed the sweep, the design would be broken. The
/// database is never emptied; every test has its own client and event.
/// </summary>
public sealed class ExpiryWithoutTheSweepTests(InventoryDatabase database) : IClassFixture<InventoryDatabase>
{
    private const int ConcurrentAttempts = 30;

    private readonly Guid _eventId = Guid.NewGuid();
    private readonly Guid _clientA = Guid.NewGuid();
    private readonly Guid _clientB = Guid.NewGuid();

    private readonly DateTime _now = Now();

    private readonly DbContextOptions<InventoryDbContext> _options = database.Options;

    private DateTime LapsedAt => _now - Seat.HoldDuration - TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Hold_WhenTheHolderLapsed_ShouldBeReclaimedWithNoSweep()
    {
        var seatId = await SeedHeldAsync(_clientA, LapsedAt);

        Assert.Equal(SeatStatus.Held, (await LoadAsync(seatId)).Status);

        var result = await HoldAsync(seatId, _clientB);

        Assert.Equal(HoldSeatOutcome.Held, result.Outcome);
        Assert.Equal(_clientB, (await LoadAsync(seatId)).HeldByClientId);
    }

    [Fact]
    public async Task Sell_WhenTheHoldLapsed_ShouldBeRefusedWithNoSweep()
    {
        var seatId = await SeedHeldAsync(_clientA, LapsedAt);

        var result = await SellAsync(seatId, _clientA);

        Assert.Equal(SellSeatOutcome.HoldExpired, result);
        Assert.Equal(SeatStatus.Held, (await LoadAsync(seatId)).Status);
    }

    /// <summary>
    /// Effectively available, but no sale without a hold (003). The row still names the lapsed
    /// holder, hence NotTheHolder.
    /// </summary>
    [Fact]
    public async Task Sell_WhenAnotherClientsHoldLapsed_ShouldStillRefuseWithNoSweep()
    {
        var seatId = await SeedHeldAsync(_clientA, LapsedAt);

        var result = await SellAsync(seatId, _clientB);

        Assert.Equal(SellSeatOutcome.NotTheHolder, result);
        Assert.NotEqual(SeatStatus.Sold, (await LoadAsync(seatId)).Status);
    }

    /// <summary>
    /// The cap is a SQL count with its own copy of the expiry rule; this checks that copy while
    /// the lapsed rows still read Held.
    /// </summary>
    [Fact]
    public async Task Hold_WhenAllOfAClientsHoldsLapsed_ShouldReopenTheCapWithNoSweep()
    {
        var cap = SeatReservationLimits.MaxHoldsPerClientPerEvent;

        for (var i = 0; i < cap; i++)
        {
            await SeedHeldAsync(_clientA, LapsedAt);
        }

        var fresh = await SeedAvailableAsync();

        await using (var context = new InventoryDbContext(_options))
        {
            Assert.Equal(
                cap,
                await context.Seats.CountAsync(seat =>
                    seat.HeldByClientId == _clientA && seat.Status == SeatStatus.Held));
        }

        var result = await HoldAsync(fresh, _clientA);

        Assert.Equal(HoldSeatOutcome.Held, result.Outcome);
    }

    [Fact]
    public async Task Hold_WhenManyClientsReclaimOneLapsedSeat_ExactlyOneShouldWinWithNoSweep()
    {
        var seatId = await SeedHeldAsync(_clientA, LapsedAt);

        var contexts = new List<InventoryDbContext>(ConcurrentAttempts);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var attempts = new List<Task<HoldSeatResult>>(ConcurrentAttempts);

            for (var i = 0; i < ConcurrentAttempts; i++)
            {
                var context = new InventoryDbContext(_options);
                contexts.Add(context);

                var handler = new HoldSeatCommandHandler(
                    new EfSeatRepository(context),
                    new AlwaysGrantingLock(),
                    new FakeTimeProvider(_now));

                var command = new HoldSeatCommand(_eventId, seatId, Guid.NewGuid());

                attempts.Add(Task.Run(async () =>
                {
                    await gate.Task;
                    return await handler.HandleAsync(command);
                }));
            }

            gate.SetResult();
            var results = await Task.WhenAll(attempts);

            var won = results.Count(result => result.Outcome is HoldSeatOutcome.Held);

            Assert.Equal(1, won);

            // No racer may read the row mid-flight as missing.
            Assert.All(results, result => Assert.Contains(result.Outcome, new[]
            {
                HoldSeatOutcome.Held,
                HoldSeatOutcome.AlreadyHeld,
                HoldSeatOutcome.LostRace
            }));

            var seat = await LoadAsync(seatId);

            Assert.Equal(SeatStatus.Held, seat.Status);
            Assert.NotEqual(_clientA, seat.HeldByClientId);
            Assert.Equal(_now + Seat.HoldDuration, seat.HoldExpiresAt);
        }
        finally
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync();
            }
        }
    }

    private async Task<HoldSeatResult> HoldAsync(Guid seatId, Guid clientId)
    {
        await using var context = new InventoryDbContext(_options);

        var handler = new HoldSeatCommandHandler(
            new EfSeatRepository(context),
            new AlwaysGrantingLock(),
            new FakeTimeProvider(_now));

        return await handler.HandleAsync(new HoldSeatCommand(_eventId, seatId, clientId));
    }

    private async Task<SellSeatOutcome> SellAsync(Guid seatId, Guid clientId)
    {
        await using var context = new InventoryDbContext(_options);

        var handler = new SellSeatCommandHandler(
            new EfSeatRepository(context),
            new FakeTimeProvider(_now));

        return await handler.HandleAsync(new SellSeatCommand(_eventId, seatId, clientId));
    }

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

    private async Task<Guid> SeedAvailableAsync()
    {
        var seatId = Guid.NewGuid();

        await using var context = new InventoryDbContext(_options);
        context.Seats.Add(Seat.Create(seatId, _eventId));
        await context.SaveChangesAsync();

        return seatId;
    }

    private async Task<Seat> LoadAsync(Guid seatId)
    {
        await using var context = new InventoryDbContext(_options);

        return await context.Seats.AsNoTracking().SingleAsync(seat => seat.Id == seatId);
    }

    // Truncated to microseconds to survive the Postgres round trip.
    private static DateTime Now()
    {
        var utcNow = DateTime.UtcNow;

        return new DateTime(utcNow.Ticks - utcNow.Ticks % TimeSpan.TicksPerMicrosecond, DateTimeKind.Utc);
    }
}
