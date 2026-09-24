using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>
/// The sale under contention, against real Postgres and with no Redis lock: the <c>xmin</c>
/// token alone must prevent a double sale.
/// </summary>
public sealed class ConcurrentSellTests(InventoryDatabase database) : IClassFixture<InventoryDatabase>
{
    /// <summary>How many times one impatient client submits the checkout form.</summary>
    private const int ConcurrentSubmissions = 50;

    private readonly Guid _eventId = Guid.NewGuid();
    private readonly Guid _clientA = Guid.NewGuid();
    private readonly Guid _clientB = Guid.NewGuid();

    private readonly DbContextOptions<InventoryDbContext> _options = database.Options;

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

    /// <summary>Truncated to microseconds, the resolution Postgres stores.</summary>
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

    /// <summary>One client, fifty simultaneous submissions, one seat: exactly one write lands.</summary>
    [Fact]
    public async Task Sell_WhenOneClientSubmitsCheckoutManyTimes_ShouldWriteTheSaleExactlyOnce()
    {
        var heldAt = Truncate(DateTime.UtcNow);
        var seatId = await SeedHeldSeatAsync(_clientA, heldAt);
        var sellingAt = heldAt.AddMinutes(1);

        var contexts = new List<InventoryDbContext>(ConcurrentSubmissions);
        var sessions = new List<(EfSeatRepository Repository, Seat Seat)>(ConcurrentSubmissions);

        // All load before the gate, so all collide on the write.
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
    /// A hold lapsing at the instant its owner checks out while another client reclaims it. Both
    /// are legal transitions; only the row version can separate them, and the seat must end up
    /// one coherent state.
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

        // Whichever won, the row reads as one coherent state.
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
