using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Application;
using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Ports;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>
/// Expiry with no sweep and no Redis: if any of these needed the sweep, the design would be
/// broken. Lapsed rows stay <c>Held</c> in Postgres throughout, and the invariants hold anyway.
/// Covers reclaiming, selling against a lapsed hold, the hold cap and the oversell invariant.
/// </summary>
public sealed class ExpiryWithoutTheSweepTests : IAsyncLifetime
{
    /// <summary>How many clients pile onto the one lapsed seat.</summary>
    private const int ConcurrentAttempts = 30;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("encore")
        .WithUsername("encore")
        .WithPassword("encore")
        .Build();

    private readonly Guid _eventId = Guid.NewGuid();
    private readonly Guid _clientA = Guid.NewGuid();
    private readonly Guid _clientB = Guid.NewGuid();

    /// <summary>Truncated to whole microseconds; see <c>ConcurrentHoldTests</c>.</summary>
    private readonly DateTime _now = new(DateTime.UtcNow.Ticks / 10 * 10, DateTimeKind.Utc);

    private DbContextOptions<InventoryDbContext> _options = null!;

    /// <summary>A hold taken this long ago has lapsed by <see cref="_now"/>.</summary>
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

    /// <summary>The row still says Held by someone else, and the next client gets the seat.</summary>
    [Fact]
    public async Task Hold_WhenTheHolderLapsed_ShouldBeReclaimedWithNoSweep()
    {
        var seatId = await SeedHeldAsync(_clientA, LapsedAt);

        // The stale row is still stale: nothing tidied it.
        Assert.Equal(SeatStatus.Held, (await LoadAsync(seatId)).Status);

        var result = await HoldAsync(seatId, _clientB);

        Assert.Equal(HoldSeatOutcome.Held, result.Outcome);
        Assert.Equal(_clientB, (await LoadAsync(seatId)).HeldByClientId);
    }

    /// <summary>The lapsed holder cannot sell.</summary>
    [Fact]
    public async Task Sell_WhenTheHoldLapsed_ShouldBeRefusedWithNoSweep()
    {
        var seatId = await SeedHeldAsync(_clientA, LapsedAt);

        var result = await SellAsync(seatId, _clientA);

        Assert.Equal(SellSeatOutcome.HoldExpired, result.Outcome);
        Assert.Equal(SeatStatus.Held, (await LoadAsync(seatId)).Status);
    }

    /// <summary>
    /// A passer-by cannot buy an effectively available seat without holding it first; the
    /// refusal is NotTheHolder because the row still names the old holder.
    /// </summary>
    [Fact]
    public async Task Sell_WhenAnotherClientsHoldLapsed_ShouldStillRefuseWithNoSweep()
    {
        var seatId = await SeedHeldAsync(_clientA, LapsedAt);

        var result = await SellAsync(seatId, _clientB);

        Assert.Equal(SellSeatOutcome.NotTheHolder, result.Outcome);
        Assert.NotEqual(SeatStatus.Sold, (await LoadAsync(seatId)).Status);
    }

    /// <summary>
    /// The hold cap reopens as holds lapse. The cap is a SQL count with its own copy of the
    /// expiry rule, so this checks that copy while all four stale rows still read Held.
    /// </summary>
    [Fact]
    public async Task Hold_WhenAllOfAClientsHoldsLapsed_ShouldReopenTheCapWithNoSweep()
    {
        var cap = HoldSeatCommandHandler.MaxHoldsPerClientPerEvent;

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

    /// <summary>
    /// Thirty clients reclaim one lapsed seat at once and exactly one gets it, with no lock and
    /// no sweep.
    /// </summary>
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
                    new FixedTimeProvider(_now));

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

            // Nothing threw, and no racer read a row mid-flight as missing.
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
            new FixedTimeProvider(_now));

        return await handler.HandleAsync(new HoldSeatCommand(_eventId, seatId, clientId));
    }

    private async Task<SellSeatResult> SellAsync(Guid seatId, Guid clientId)
    {
        await using var context = new InventoryDbContext(_options);

        var handler = new SellSeatCommandHandler(
            new EfSeatRepository(context),
            new FixedTimeProvider(_now));

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

    /// <summary>A lock that grants everything, so the results rest on the aggregate and xmin alone.</summary>
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

    /// <summary>A clock that does not move, so "lapsed" comes from the seeded data.</summary>
    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
