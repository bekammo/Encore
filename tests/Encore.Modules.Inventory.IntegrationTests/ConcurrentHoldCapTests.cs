using Encore.Modules.Inventory.Adapters.Caching;
using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Application;
using Encore.Modules.Inventory.Contracts;
using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using StackExchange.Redis;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>
/// The cap spans rows, so the client lock is its only guard (005). Postgres and Redis are never
/// emptied: every count and lock key names the test's own client and event.
/// </summary>
public sealed class ConcurrentHoldCapTests(InventoryDatabase database, InventoryRedis redis)
    : IClassFixture<InventoryDatabase>, IClassFixture<InventoryRedis>, IAsyncLifetime
{
    private const int ConcurrentAttempts = 12;

    private readonly Guid _eventId = Guid.NewGuid();
    private readonly Guid _clientA = Guid.NewGuid();

    private readonly DbContextOptions<InventoryDbContext> _options = database.Options;
    private readonly IConnectionMultiplexer _connection = redis.Connection;

    // Per test, so its advisory-lock sessions end with the test.
    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(database.ConnectionString);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _dataSource.DisposeAsync();

    private async Task<List<Guid>> SeedAvailableSeatsAsync(int count)
    {
        var seatIds = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList();

        await using var context = new InventoryDbContext(_options);
        foreach (var seatId in seatIds)
        {
            context.Seats.Add(Seat.Create(seatId, _eventId));
        }

        await context.SaveChangesAsync();
        return seatIds;
    }

    private async Task<IReadOnlyList<HoldSeatResult>> RaceForSeatsAsync(
        List<Guid> seatIds,
        IDistributedLock distributedLock)
    {
        var contexts = new List<InventoryDbContext>(seatIds.Count);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var attempts = new List<Task<HoldSeatResult>>(seatIds.Count);

            foreach (var seatId in seatIds)
            {
                var context = new InventoryDbContext(_options);
                contexts.Add(context);

                var handler = new HoldSeatCommandHandler(
                    new EfSeatRepository(context),
                    distributedLock,
                    TimeProvider.System);

                var command = new HoldSeatCommand(_eventId, seatId, _clientA);

                attempts.Add(Task.Run(async () =>
                {
                    await gate.Task;
                    return await handler.HandleAsync(command);
                }));
            }

            gate.SetResult();
            return await Task.WhenAll(attempts);
        }
        finally
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync();
            }
        }
    }

    private async Task<int> CountPersistedHoldsAsync()
    {
        await using var context = new InventoryDbContext(_options);

        return await context.Seats.CountAsync(seat =>
            seat.EventId == _eventId
            && seat.HeldByClientId == _clientA
            && seat.Status == SeatStatus.Held);
    }

    /// <summary>
    /// Lock losers are refused, so a burst proves only a ceiling; the next test proves the cap is
    /// reached.
    /// </summary>
    [Theory]
    [InlineData(Redis)]
    [InlineData(Postgres)]
    public async Task Hold_WhenOneClientRacesThemselves_ShouldNotExceedTheCap(string serialisedBy)
    {
        var seatIds = await SeedAvailableSeatsAsync(ConcurrentAttempts);

        var results = await RaceForSeatsAsync(seatIds, LockFor(serialisedBy));
        var held = results.Count(result => result.Outcome is HoldSeatOutcome.Held);

        Assert.True(
            held <= SeatReservationLimits.MaxHoldsPerClientPerEvent,
            $"Client held {held} seats; the cap is {SeatReservationLimits.MaxHoldsPerClientPerEvent}. "
            + $"Outcomes: {Describe(results)}");

        Assert.All(results, result => Assert.Contains(result.Outcome, new[]
        {
            HoldSeatOutcome.Held,
            HoldSeatOutcome.HoldCapReached,
            HoldSeatOutcome.ConcurrentRequestInFlight
        }));

        Assert.Equal(held, await CountPersistedHoldsAsync());
    }

    [Theory]
    [InlineData(Redis)]
    [InlineData(Postgres)]
    public async Task Hold_WhenOneClientHoldsSeatAfterSeat_ShouldReachExactlyTheCap(string serialisedBy)
    {
        var seatIds = await SeedAvailableSeatsAsync(ConcurrentAttempts);
        var distributedLock = LockFor(serialisedBy);

        await using var context = new InventoryDbContext(_options);
        var handler = new HoldSeatCommandHandler(
            new EfSeatRepository(context), distributedLock, TimeProvider.System);

        var held = 0;

        foreach (var seatId in seatIds)
        {
            var result = await handler.HandleAsync(new HoldSeatCommand(_eventId, seatId, _clientA));

            if (result.Outcome is HoldSeatOutcome.Held)
            {
                held++;
            }
        }

        Assert.Equal(SeatReservationLimits.MaxHoldsPerClientPerEvent, held);
        Assert.Equal(SeatReservationLimits.MaxHoldsPerClientPerEvent, await CountPersistedHoldsAsync());
    }

    private const string Redis = nameof(Redis);
    private const string Postgres = nameof(Postgres);

    private IDistributedLock LockFor(string serialisedBy) => serialisedBy switch
    {
        Redis => new RedisDistributedLock(_connection, NullLogger<RedisDistributedLock>.Instance),
        Postgres => new PostgresAdvisoryLock(_dataSource),
        _ => throw new ArgumentOutOfRangeException(nameof(serialisedBy), serialisedBy, null)
    };

    private static string Describe(IReadOnlyList<HoldSeatResult> results) =>
        string.Join(", ", results
            .GroupBy(result => result.Outcome)
            .Select(group => $"{group.Key}={group.Count()}"));
}
