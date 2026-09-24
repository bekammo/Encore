using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>No lock: <c>xmin</c> alone settles the race (004).</summary>
public sealed class ConcurrentHoldTests(InventoryDatabase database)
    : IClassFixture<InventoryDatabase>, IAsyncLifetime
{
    private const int ConcurrentAttempts = 50;

    private readonly Guid _seatId = Guid.NewGuid();
    private readonly Guid _eventId = Guid.NewGuid();

    private readonly DbContextOptions<InventoryDbContext> _options = database.Options;

    private enum Outcome
    {
        Won,
        LostRace,
        Refused,
        Unexpected
    }

    public async Task InitializeAsync()
    {
        await using var context = new InventoryDbContext(_options);

        context.Seats.Add(Seat.Create(_seatId, _eventId));
        await context.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Hold_WhenManyClientsRaceForTheSameSeat_ExactlyOneShouldWin()
    {
        var now = Now();
        var contexts = new List<InventoryDbContext>(ConcurrentAttempts);
        var sessions = new List<(EfSeatRepository Repository, Seat Seat, Guid ClientId)>(ConcurrentAttempts);

        // Every attempt loads the seat now, so all share one row version.
        for (var i = 0; i < ConcurrentAttempts; i++)
        {
            var context = new InventoryDbContext(_options);
            contexts.Add(context);

            var repository = new EfSeatRepository(context);
            var seat = await repository.GetByIdAsync(_seatId);

            sessions.Add((repository, seat!, Guid.NewGuid()));
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
            catch (SeatTransitionException ex) when (ex.Reason == SeatTransitionReason.SeatAlreadyHeld)
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
                () => session.Seat.Hold(session.ClientId, now),
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

        // Losers lose on the write, never in the aggregate.
        Assert.Equal(ConcurrentAttempts - 1, outcomes.Count(outcome => outcome == Outcome.LostRace));
        Assert.Equal(0, outcomes.Count(outcome => outcome == Outcome.Refused));

        await using var verification = new InventoryDbContext(_options);
        var persisted = await verification.Seats.SingleAsync(seat => seat.Id == _seatId);

        Assert.Equal(SeatStatus.Held, persisted.Status);
        Assert.NotNull(persisted.HeldByClientId);
        Assert.Equal(now.AddMinutes(5), persisted.HoldExpiresAt);

        var winner = sessions.Single(session =>
            session.Seat.Status == SeatStatus.Held
            && session.Seat.HeldByClientId == persisted.HeldByClientId);
        Assert.Equal(winner.ClientId, persisted.HeldByClientId);
    }

    [Fact]
    public async Task Hold_WhenClientsLoadAfterTheSeatIsTaken_ShouldAllBeRefusedByTheAggregate()
    {
        var now = Now();
        var winnerClientId = Guid.NewGuid();

        await using (var winnerContext = new InventoryDbContext(_options))
        {
            var winnerRepository = new EfSeatRepository(winnerContext);
            var winnerSeat = await winnerRepository.GetByIdAsync(_seatId);

            winnerSeat!.Hold(winnerClientId, now);
            await winnerRepository.SaveAsync(winnerSeat);
        }

        var contexts = new List<InventoryDbContext>(ConcurrentAttempts);
        var sessions = new List<(EfSeatRepository Repository, Seat Seat, Guid ClientId)>(ConcurrentAttempts);

        for (var i = 0; i < ConcurrentAttempts; i++)
        {
            var context = new InventoryDbContext(_options);
            contexts.Add(context);

            var repository = new EfSeatRepository(context);
            var seat = await repository.GetByIdAsync(_seatId);

            sessions.Add((repository, seat!, Guid.NewGuid()));
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
            catch (SeatTransitionException ex) when (ex.Reason == SeatTransitionReason.SeatAlreadyHeld)
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
                () => session.Seat.Hold(session.ClientId, now),
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

        Assert.Equal(ConcurrentAttempts, outcomes.Count(outcome => outcome == Outcome.Refused));
        Assert.Equal(0, outcomes.Count(outcome => outcome == Outcome.Won));
        Assert.Equal(0, outcomes.Count(outcome => outcome == Outcome.LostRace));

        await using var verification = new InventoryDbContext(_options);
        var persisted = await verification.Seats.SingleAsync(seat => seat.Id == _seatId);

        Assert.Equal(SeatStatus.Held, persisted.Status);
        Assert.Equal(winnerClientId, persisted.HeldByClientId);
        Assert.Equal(now.AddMinutes(5), persisted.HoldExpiresAt);
    }

    // Truncated to microseconds to survive the Postgres round trip.
    private static DateTime Now()
    {
        var utcNow = DateTime.UtcNow;

        return new DateTime(utcNow.Ticks - utcNow.Ticks % TimeSpan.TicksPerMicrosecond, DateTimeKind.Utc);
    }
}
