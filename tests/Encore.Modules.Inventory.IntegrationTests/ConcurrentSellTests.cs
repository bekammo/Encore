using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>No lock: <c>xmin</c> alone prevents a double sale (004).</summary>
public sealed class ConcurrentSellTests(InventoryDatabase database) : IClassFixture<InventoryDatabase>
{
    private const int ConcurrentSubmissions = 50;

    private readonly Guid _eventId = Guid.NewGuid();
    private readonly Guid _clientA = Guid.NewGuid();
    private readonly Guid _clientB = Guid.NewGuid();

    private readonly DbContextOptions<InventoryDbContext> _options = database.Options;

    private enum Outcome
    {
        Won,
        LostRace,
        Refused,
        Unexpected
    }

    // Truncated to microseconds to survive the Postgres round trip.
    private static DateTime Truncate(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerMicrosecond, DateTimeKind.Utc);

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

        var attempts = sessions
            .Select(session => AttemptAsync(
                () => session.Seat.Sell(_clientA, sellingAt),
                session.Repository,
                session.Seat))
            .ToArray();

        startGate.SetResult();
        var outcomes = await Task.WhenAll(attempts);

        foreach (var context in contexts)
        {
            await context.DisposeAsync();
        }

        Assert.True(
            unexpected.Count == 0,
            $"Attempts failed in unsanctioned ways: {string.Join(" | ", unexpected.Select(exception => exception.GetType().Name + ": " + exception.Message))}");

        Assert.Equal(1, outcomes.Count(outcome => outcome == Outcome.Won));
        Assert.Equal(ConcurrentSubmissions - 1, outcomes.Count(outcome => outcome == Outcome.LostRace));

        await using var verification = new InventoryDbContext(_options);
        var persisted = await verification.Seats.SingleAsync(seat => seat.Id == seatId);

        Assert.Equal(SeatStatus.Sold, persisted.Status);
        Assert.Equal(_clientA, persisted.HeldByClientId);
        Assert.Null(persisted.HoldExpiresAt);
    }

    /// <summary>Sale and reclaim are both legal at their own clocks; only <c>xmin</c> separates them.</summary>
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
        var reclaim = AttemptAsync(
            () => reclaimerSeat!.Hold(_clientB, reclaimingAt),
            reclaimerRepository,
            reclaimerSeat!);

        startGate.SetResult();
        var outcomes = await Task.WhenAll(sale, reclaim);

        Assert.True(
            unexpected.Count == 0,
            $"Attempts failed in unsanctioned ways: {string.Join(" | ", unexpected.Select(exception => exception.GetType().Name + ": " + exception.Message))}");

        Assert.Equal(1, outcomes.Count(outcome => outcome == Outcome.Won));
        Assert.Equal(1, outcomes.Count(outcome => outcome == Outcome.LostRace));

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
