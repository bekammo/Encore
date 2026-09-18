using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>
/// The sale under contention. Holding the wrong seat costs somebody a refund
/// email; selling the same seat twice puts two people outside a sold-out venue
/// holding valid receipts, which is the failure this architecture exists to make
/// impossible.
/// </summary>
/// <remarks>
/// Real Postgres via Testcontainers, and no Redis lock anywhere — as with
/// <see cref="ConcurrentHoldTests"/>, the point is that the <c>xmin</c>
/// concurrency token carries the invariant on its own. One container serves both
/// facts; each seeds its own seat so they cannot interfere.
/// </remarks>
public sealed class ConcurrentSellTests : IAsyncLifetime
{
    /// <summary>How many times one impatient client submits the checkout form.</summary>
    private const int ConcurrentSubmissions = 50;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("encore")
        .WithUsername("encore")
        .WithPassword("encore")
        .Build();

    private readonly Guid _eventId = Guid.NewGuid();
    private readonly Guid _clientA = Guid.NewGuid();
    private readonly Guid _clientB = Guid.NewGuid();

    private DbContextOptions<InventoryDbContext> _options = null!;

    private enum Outcome
    {
        /// <summary>This attempt wrote the row.</summary>
        Won,

        /// <summary>Lost the optimistic-concurrency race on the write.</summary>
        LostRace,

        /// <summary>The aggregate refused before any write was attempted.</summary>
        Refused,

        /// <summary>Failed in a way this test does not sanction.</summary>
        Unexpected
    }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        _options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;

        await using var context = new InventoryDbContext(_options);
        await context.Database.MigrateAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    /// <summary>
    /// Postgres timestamptz resolves to a microsecond; DateTime ticks are 100ns.
    /// Truncating up front keeps round-tripped instants exactly comparable.
    /// </summary>
    private static DateTime Truncate(DateTime value) =>
        new(value.Ticks / 10 * 10, DateTimeKind.Utc);

    /// <summary>Creates a seat already held by <paramref name="clientId"/> at <paramref name="heldAt"/>.</summary>
    private async Task<Guid> SeedHeldSeatAsync(Guid clientId, DateTime heldAt)
    {
        var seatId = Guid.NewGuid();

        await using var context = new InventoryDbContext(_options);

        var seat = Seat.Create(seatId, _eventId);
        seat.Hold(clientId, heldAt);

        context.Seats.Add(seat);
        await context.SaveChangesAsync();

        return seatId;
    }

    /// <summary>
    /// One client, fifty simultaneous checkout submissions, one seat. Exactly one
    /// write may reach the row — the other forty-nine must be refused by the
    /// database, not by luck.
    /// </summary>
    [Fact]
    public async Task Sell_WhenOneClientSubmitsCheckoutManyTimes_ShouldWriteTheSaleExactlyOnce()
    {
        var heldAt = Truncate(DateTime.UtcNow);
        var seatId = await SeedHeldSeatAsync(_clientA, heldAt);
        var sellingAt = heldAt.AddMinutes(1);

        var contexts = new List<InventoryDbContext>(ConcurrentSubmissions);
        var sessions = new List<(EfSeatRepository Repository, Seat Seat)>(ConcurrentSubmissions);

        // Every submission loads before the gate opens, so all fifty carry the
        // same row version and collide on the write rather than queueing.
        for (var i = 0; i < ConcurrentSubmissions; i++)
        {
            var context = new InventoryDbContext(_options);
            contexts.Add(context);

            var repository = new EfSeatRepository(context);
            var seat = await repository.GetByIdAsync(seatId);

            sessions.Add((repository, seat!));
        }

        var unexpected = new List<Exception>();
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = sessions.Select(async session =>
        {
            await startGate.Task;

            try
            {
                session.Seat.Sell(_clientA, sellingAt);
                await session.Repository.SaveAsync(session.Seat);
                return Outcome.Won;
            }
            catch (ConcurrentSeatModificationException)
            {
                return Outcome.LostRace;
            }
            catch (SeatTransitionException)
            {
                return Outcome.Refused;
            }
            catch (Exception ex)
            {
                lock (unexpected)
                {
                    unexpected.Add(ex);
                }

                return Outcome.Unexpected;
            }
        }).ToArray();

        startGate.SetResult();
        var outcomes = await Task.WhenAll(attempts);

        foreach (var context in contexts)
        {
            await context.DisposeAsync();
        }

        Assert.True(
            unexpected.Count == 0,
            $"Attempts failed in unsanctioned ways: {string.Join(" | ", unexpected.Select(e => e.GetType().Name + ": " + e.Message))}");

        Assert.Equal(1, outcomes.Count(o => o == Outcome.Won));
        Assert.Equal(ConcurrentSubmissions - 1, outcomes.Count(o => o == Outcome.LostRace));

        await using var verification = new InventoryDbContext(_options);
        var persisted = await verification.Seats.SingleAsync(seat => seat.Id == seatId);

        Assert.Equal(SeatStatus.Sold, persisted.Status);
        Assert.Equal(_clientA, persisted.HeldByClientId);
        Assert.Null(persisted.HoldExpiresAt);
    }

    /// <summary>
    /// The genuinely nasty one: a hold lapsing at the same instant its owner
    /// completes checkout. Client A selling inside their hold and Client B
    /// reclaiming the lapsed hold are <em>both</em> legal transitions in the
    /// aggregate — they disagree only about what time it is. Nothing in the domain
    /// can separate them, so the row version has to, and the seat must end up
    /// unambiguously one thing or the other.
    /// </summary>
    [Fact]
    public async Task Sell_WhenRacingAReclaimOfTheExpiringHold_ExactlyOneShouldWin()
    {
        var heldAt = Truncate(DateTime.UtcNow);
        var seatId = await SeedHeldSeatAsync(_clientA, heldAt);

        // Inside the hold for the seller, past it for the reclaimer.
        var sellingAt = heldAt.AddMinutes(1);
        var reclaimingAt = heldAt.AddMinutes(6);

        await using var sellerContext = new InventoryDbContext(_options);
        await using var reclaimerContext = new InventoryDbContext(_options);

        var sellerRepository = new EfSeatRepository(sellerContext);
        var reclaimerRepository = new EfSeatRepository(reclaimerContext);

        var sellerSeat = await sellerRepository.GetByIdAsync(seatId);
        var reclaimerSeat = await reclaimerRepository.GetByIdAsync(seatId);

        var unexpected = new List<Exception>();
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Outcome> AttemptAsync(Action transition, EfSeatRepository repository, Seat seat)
        {
            await startGate.Task;

            try
            {
                transition();
                await repository.SaveAsync(seat);
                return Outcome.Won;
            }
            catch (ConcurrentSeatModificationException)
            {
                return Outcome.LostRace;
            }
            catch (SeatTransitionException)
            {
                return Outcome.Refused;
            }
            catch (Exception ex)
            {
                lock (unexpected)
                {
                    unexpected.Add(ex);
                }

                return Outcome.Unexpected;
            }
        }

        var sale = AttemptAsync(() => sellerSeat!.Sell(_clientA, sellingAt), sellerRepository, sellerSeat!);
        var reclaim = AttemptAsync(() => reclaimerSeat!.Hold(_clientB, reclaimingAt), reclaimerRepository, reclaimerSeat!);

        startGate.SetResult();
        var outcomes = await Task.WhenAll(sale, reclaim);

        Assert.True(
            unexpected.Count == 0,
            $"Attempts failed in unsanctioned ways: {string.Join(" | ", unexpected.Select(e => e.GetType().Name + ": " + e.Message))}");

        Assert.Equal(1, outcomes.Count(o => o == Outcome.Won));
        Assert.Equal(1, outcomes.Count(o => o == Outcome.LostRace));

        // Whichever won, the row must read as exactly one coherent state — never a
        // sale wearing a reclaimed hold's expiry, or the reverse.
        await using var verification = new InventoryDbContext(_options);
        var persisted = await verification.Seats.SingleAsync(seat => seat.Id == seatId);

        if (persisted.Status is SeatStatus.Sold)
        {
            Assert.Equal(_clientA, persisted.HeldByClientId);
            Assert.Null(persisted.HoldExpiresAt);
        }
        else
        {
            Assert.Equal(SeatStatus.Held, persisted.Status);
            Assert.Equal(_clientB, persisted.HeldByClientId);
            Assert.Equal(reclaimingAt + Seat.HoldDuration, persisted.HoldExpiresAt);
        }
    }
}
