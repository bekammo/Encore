using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Application;
using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Ports;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>
/// Expiry with no sweep anywhere: the falsification test <c>DECISIONS.md</c> 007
/// asks for, and 062 finally writes.
/// </summary>
/// <remarks>
/// <para>
/// <b>What makes this falsifiable rather than decorative.</b> 007 states the
/// criterion as a challenge — "if a test cannot pass with the sweep disabled, the
/// sweep has become load-bearing and the design is broken" — and until the sweep
/// existed, that was satisfied trivially by there being nothing to disable. Now
/// there is, so the claim needs a test that would actually fail if it stopped
/// being true. <see cref="ExpiredHoldSweeper"/> is not referenced anywhere in this
/// file, not registered, and not constructed. Every row here whose hold has lapsed
/// still reads <c>Held</c> in Postgres for the whole test, and the invariants hold
/// anyway.
/// </para>
/// <para>
/// <b>Redis is absent too, and that is not scope creep.</b> The claim under test
/// is that correctness comes from the aggregate and from <c>xmin</c>; a lock that
/// serialised the racers would hide whether the seat rules or the lock produced
/// the result. The lock here always grants, so it protects nothing —
/// <see cref="ConcurrentHoldTests"/> makes the same choice for the same reason.
/// </para>
/// <para>
/// <b>These four are the paths that could plausibly have grown a dependency on the
/// sweep</b>, rather than a sample: reclaiming a lapsed hold, selling against one,
/// the multi-row hold cap, and the oversell invariant itself. The cap is the least
/// obvious and the most valuable — it is counted in SQL rather than in the
/// aggregate, so it is the one place where a second copy of the expiry rule could
/// have gone stale without anybody noticing until a client was capped forever.
/// </para>
/// </remarks>
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

    /// <summary>
    /// The row still says <c>Held</c> by somebody else, and the next client gets
    /// the seat anyway.
    /// </summary>
    [Fact]
    public async Task Hold_WhenTheHolderLapsed_ShouldBeReclaimedWithNoSweep()
    {
        var seatId = await SeedHeldAsync(_clientA, LapsedAt);

        // The stale row is still stale: nothing has tidied it, and nothing will.
        Assert.Equal(SeatStatus.Held, (await LoadAsync(seatId)).Status);

        var result = await HoldAsync(seatId, _clientB);

        Assert.Equal(HoldSeatOutcome.Held, result.Outcome);
        Assert.Equal(_clientB, (await LoadAsync(seatId)).HeldByClientId);
    }

    /// <summary>
    /// The lapsed holder cannot sell, which is the invariant a sweep-driven design
    /// would have made depend on a timer.
    /// </summary>
    [Fact]
    public async Task Sell_WhenTheHoldLapsed_ShouldBeRefusedWithNoSweep()
    {
        var seatId = await SeedHeldAsync(_clientA, LapsedAt);

        var result = await SellAsync(seatId, _clientA);

        Assert.Equal(SellSeatOutcome.HoldExpired, result.Outcome);
        Assert.Equal(SeatStatus.Held, (await LoadAsync(seatId)).Status);
    }

    /// <summary>
    /// The seat is effectively available because somebody else's hold lapsed, and
    /// a passer-by still cannot buy it without holding it first.
    /// </summary>
    /// <remarks>
    /// 007's no-direct-Available-to-Sold rule, checked on the one path where the
    /// row's column and its truth disagree — and this is the case that makes the
    /// rule matter, because if a lapsed hold could be sold around, the hold step
    /// would be skippable by simply waiting five minutes. The refusal is
    /// <c>NotTheHolder</c> rather than <c>NoActiveHold</c>: the row still names
    /// <c>_clientA</c>, and 041 records why the reason turns on who is asking.
    /// </remarks>
    [Fact]
    public async Task Sell_WhenAnotherClientsHoldLapsed_ShouldStillRefuseWithNoSweep()
    {
        var seatId = await SeedHeldAsync(_clientA, LapsedAt);

        var result = await SellAsync(seatId, _clientB);

        Assert.Equal(SellSeatOutcome.NotTheHolder, result.Outcome);
        Assert.NotEqual(SeatStatus.Sold, (await LoadAsync(seatId)).Status);
    }

    /// <summary>
    /// The hold cap frees itself as holds lapse, with no sweep to free it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The subtlest of the four. The cap spans rows, so it is a Postgres
    /// <c>COUNT</c> rather than an aggregate rule (006) — which means the lapsed-hold
    /// rule exists there in a second form, in SQL. If that copy said only
    /// <c>Status = Held</c>, this client would be capped until a background job
    /// happened to run, and every test that did not involve waiting would still
    /// pass. <c>FindLiveHoldsAsync</c> puts <c>HoldExpiresAt &gt; utcNow</c> in the
    /// predicate, so the count is of live holds rather than of rows, and the cap
    /// reopens the moment the holds lapse rather than the moment somebody tidies up.
    /// </para>
    /// <para>
    /// Every one of the four stale rows is still <c>Held</c> in the database when
    /// the fifth hold succeeds, which the assertion checks rather than assumes.
    /// </para>
    /// </remarks>
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
    /// The headline invariant, on the path where the sweep might have been load
    /// bearing: thirty clients reclaim one lapsed seat at once and exactly one
    /// gets it.
    /// </summary>
    /// <remarks>
    /// <see cref="ConcurrentHoldTests"/> proves this for an available seat. This is
    /// the same proof for a seat that is only <i>effectively</i> available — where
    /// every racer must first agree that a hold it can see in the row has lapsed,
    /// and then race on <c>xmin</c> like everybody else. No lock, no sweep: if
    /// expiry needed either of them to be a real state change, this is where two
    /// winners would appear.
    /// </remarks>
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

            // Nobody failed in a way this test does not sanction — in particular
            // nothing threw, and nothing came back SeatNotFound because a racer
            // read a row mid-flight.
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

    /// <summary>
    /// A lock that grants everything, which is to say no lock at all.
    /// </summary>
    /// <remarks>
    /// Present because the handlers take the port, not because anything here wants
    /// serialising. Every racer holds it at once, so whatever these tests prove is
    /// proved by the aggregate and by <c>xmin</c>.
    /// </remarks>
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

    /// <summary>
    /// A clock that does not move, so "lapsed" is a property of the seeded data
    /// rather than of how long the test took.
    /// </summary>
    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
