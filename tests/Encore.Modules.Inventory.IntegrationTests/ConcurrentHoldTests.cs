using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>
/// Fifty clients grab the same seat at once and exactly one gets it. Real Postgres and no Redis
/// lock, so the result rests on the <c>xmin</c> token alone.
/// </summary>
public sealed class ConcurrentHoldTests : IAsyncLifetime
{
    /// <summary>How many clients pile onto the one seat.</summary>
    private const int ConcurrentAttempts = 50;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("encore")
        .WithUsername("encore")
        .WithPassword("encore")
        .Build();

    private readonly Guid _seatId = Guid.NewGuid();
    private readonly Guid _eventId = Guid.NewGuid();

    private DbContextOptions<InventoryDbContext> _options = null!;

    /// <summary>How each attempt ended. Anything but these four is a test failure.</summary>
    private enum Outcome
    {
        /// <summary>Took the seat.</summary>
        Won,

        /// <summary>Lost the optimistic-concurrency race on the write.</summary>
        LostRace,

        /// <summary>The aggregate refused: someone else already held it.</summary>
        Refused,

        /// <summary>Failed in a way this test does not sanction.</summary>
        Unexpected
    }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        _options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInventoryNpgsql(_postgres.GetConnectionString())
            .Options;

        await using var context = new InventoryDbContext(_options);

        // Migrate rather than EnsureCreated, so the real migration is exercised.
        await context.Database.MigrateAsync();

        context.Seats.Add(Seat.Create(_seatId, _eventId));
        await context.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task Hold_WhenManyClientsRaceForTheSameSeat_ExactlyOneShouldWin()
    {
        // Arrange: every attempt loads the seat now, so all share one row version.
        // Truncated to microseconds to survive the Postgres round trip.
        var now = new DateTime(DateTime.UtcNow.Ticks / 10 * 10, DateTimeKind.Utc);
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

        var attempts = sessions.Select(async session =>
        {
            await startGate.Task;

            try
            {
                session.Seat.Hold(session.ClientId, now);
                await session.Repository.SaveAsync(session.Seat);
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
        // An EF Core exception here would mean the adapter stopped translating.
                lock (unexpected)
                {
                    unexpected.Add(ex);
                }

                return Outcome.Unexpected;
            }
        }).ToArray();

        // Act — release all 50 at once.
        startGate.SetResult();
        var outcomes = await Task.WhenAll(attempts);

        foreach (var context in contexts)
        {
            await context.DisposeAsync();
        }

        // Assert
        Assert.True(
            unexpected.Count == 0,
            $"Attempts failed in unsanctioned ways: {string.Join(" | ", unexpected.Select(e => e.GetType().Name + ": " + e.Message))}");

        Assert.Equal(1, outcomes.Count(o => o == Outcome.Won));

        // Every loser loses on the write, not the read: all loaded before the gate, so
        // Refused is unreachable. The sibling test below covers Refused.
        Assert.Equal(ConcurrentAttempts - 1, outcomes.Count(o => o == Outcome.LostRace));
        Assert.Equal(0, outcomes.Count(o => o == Outcome.Refused));

        // The row agrees: held by exactly one client, with a live hold.
        await using var verification = new InventoryDbContext(_options);
        var persisted = await verification.Seats.SingleAsync(seat => seat.Id == _seatId);

        Assert.Equal(SeatStatus.Held, persisted.Status);
        Assert.NotNull(persisted.HeldByClientId);
        Assert.Equal(now.AddMinutes(5), persisted.HoldExpiresAt);

        var winner = sessions.Single(s => s.Seat.Status == SeatStatus.Held && s.Seat.HeldByClientId == persisted.HeldByClientId);
        Assert.Equal(winner.ClientId, persisted.HeldByClientId);
    }

    /// <summary>
    /// The same fifty clients, loading after the seat is taken: every one is refused by the
    /// aggregate before reaching the database. When you lose depends on when you read.
    /// </summary>
    [Fact]
    public async Task Hold_WhenClientsLoadAfterTheSeatIsTaken_ShouldAllBeRefusedByTheAggregate()
    {
        // Arrange: one client takes the seat and commits before anyone reads.
        var now = new DateTime(DateTime.UtcNow.Ticks / 10 * 10, DateTimeKind.Utc);
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

        var attempts = sessions.Select(async session =>
        {
            await startGate.Task;

            try
            {
                session.Seat.Hold(session.ClientId, now);
                await session.Repository.SaveAsync(session.Seat);
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
        }).ToArray();

        // Act
        startGate.SetResult();
        var outcomes = await Task.WhenAll(attempts);

        foreach (var context in contexts)
        {
            await context.DisposeAsync();
        }

        // Assert
        Assert.True(
            unexpected.Count == 0,
            $"Attempts failed in unsanctioned ways: {string.Join(" | ", unexpected.Select(e => e.GetType().Name + ": " + e.Message))}");

        Assert.Equal(ConcurrentAttempts, outcomes.Count(o => o == Outcome.Refused));
        Assert.Equal(0, outcomes.Count(o => o == Outcome.Won));
        Assert.Equal(0, outcomes.Count(o => o == Outcome.LostRace));

        // Fifty refusals moved nothing.
        await using var verification = new InventoryDbContext(_options);
        var persisted = await verification.Seats.SingleAsync(seat => seat.Id == _seatId);

        Assert.Equal(SeatStatus.Held, persisted.Status);
        Assert.Equal(winnerClientId, persisted.HeldByClientId);
        Assert.Equal(now.AddMinutes(5), persisted.HoldExpiresAt);
    }
}
