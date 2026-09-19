using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>
/// The test this whole module exists to pass: many clients grab for the same
/// seat at the same instant, and exactly one walks away with it.
/// </summary>
/// <remarks>
/// <para>
/// Real Postgres, via Testcontainers — an in-memory provider would prove nothing
/// here, because the thing under test is the database's conditional UPDATE, not
/// the C#. <b>The Redis lock is deliberately absent.</b> Correctness is supposed
/// to come from the <c>xmin</c> concurrency token alone, so this passing without
/// a lock anywhere in sight is what turns that from a claim into evidence.
/// </para>
/// <para>
/// Every attempt loads the seat <em>before</em> the start gate opens, so all of
/// them hold the same row version and then collide on the write. Loading after
/// the gate would let some read the already-updated row and be refused by the
/// aggregate instead, which is a clean failure but a weaker proof.
/// </para>
/// </remarks>
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

        // Migrate rather than EnsureCreated: this also proves the generated
        // migration applies against real Postgres, including that it does not
        // try to create the xmin system column.
        await context.Database.MigrateAsync();

        context.Seats.Add(Seat.Create(_seatId, _eventId));
        await context.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task Hold_WhenManyClientsRaceForTheSameSeat_ExactlyOneShouldWin()
    {
        // Arrange — every attempt gets its own context and loads the seat now, so
        // all 50 are working from the same row version.
        //
        // Truncated to whole microseconds: Postgres timestamptz resolves to a
        // microsecond while DateTime ticks are 100ns, so an untruncated instant
        // cannot survive the round trip intact and the expiry assertion at the
        // end would fail on a hold that was in fact perfectly correct.
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
                // An EF Core exception reaching here would mean the adapter stopped
                // translating and the port has started leaking.
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

        // Every loser must lose on the WRITE, not on the read. All 50 loaded
        // before the gate, so all 50 hold the same row version and none of them
        // can see the winner's state — Refused is unreachable here, and asserting
        // that it is zero is what proves the arrangement described in the remarks
        // above actually held. The previous "LostRace or Refused" accepted either
        // and would have gone on passing if the loading strategy ever changed,
        // quietly turning this into a much weaker test.
        //
        // Hold_WhenClientsLoadAfterTheSeatIsTaken_ShouldAllBeRefusedByTheAggregate
        // is the other half of the pair, and the one where Refused is the only
        // legal answer.
        Assert.Equal(ConcurrentAttempts - 1, outcomes.Count(o => o == Outcome.LostRace));
        Assert.Equal(0, outcomes.Count(o => o == Outcome.Refused));

        // And the row itself agrees: held by exactly one client, with a live hold.
        await using var verification = new InventoryDbContext(_options);
        var persisted = await verification.Seats.SingleAsync(seat => seat.Id == _seatId);

        Assert.Equal(SeatStatus.Held, persisted.Status);
        Assert.NotNull(persisted.HeldByClientId);
        Assert.Equal(now.AddMinutes(5), persisted.HoldExpiresAt);

        var winner = sessions.Single(s => s.Seat.Status == SeatStatus.Held && s.Seat.HeldByClientId == persisted.HeldByClientId);
        Assert.Equal(winner.ClientId, persisted.HeldByClientId);
    }

    /// <summary>
    /// The other half of the pair: the same fifty clients, but they load the seat
    /// <em>after</em> it has been taken. Every one of them is refused by the
    /// aggregate, and not one reaches the database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Together these two tests say one thing: <b>when you lose depends on when
    /// you read.</b> A client holding a stale row version loses on the write and
    /// gets <see cref="Outcome.LostRace"/>; a client that read current state
    /// never gets as far as the write. Both are correct, and the sibling test's
    /// assertion that <see cref="Outcome.Refused"/> is zero only means something
    /// because this test shows it is reachable at all.
    /// </para>
    /// <para>
    /// It also exercises something nothing else does at this level: that
    /// <c>GetByIdAsync</c> hands the aggregate the current row, so the refusal is
    /// judged against real state rather than a cached one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Hold_WhenClientsLoadAfterTheSeatIsTaken_ShouldAllBeRefusedByTheAggregate()
    {
        // Arrange — one client takes the seat and commits, before anybody reads.
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

        // The winner's hold is exactly as they left it: fifty refusals moved
        // nothing, which is the part a refusal-shaped bug would get wrong.
        await using var verification = new InventoryDbContext(_options);
        var persisted = await verification.Seats.SingleAsync(seat => seat.Id == _seatId);

        Assert.Equal(SeatStatus.Held, persisted.Status);
        Assert.Equal(winnerClientId, persisted.HeldByClientId);
        Assert.Equal(now.AddMinutes(5), persisted.HoldExpiresAt);
    }
}
