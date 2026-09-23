using Encore.Modules.Inventory.Adapters.Caching;
using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Application;
using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>
/// The per-client hold cap under one client racing themselves across many seats. Needs Redis:
/// the cap spans rows, so the client lock is its only guard.
/// </summary>
public sealed class ConcurrentHoldCapTests : IAsyncLifetime
{
    /// <summary>Seats the client tries for at once, comfortably above the cap.</summary>
    private const int ConcurrentAttempts = 12;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("encore")
        .WithUsername("encore")
        .WithPassword("encore")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7").Build();

    private readonly Guid _eventId = Guid.NewGuid();
    private readonly Guid _clientA = Guid.NewGuid();

    private DbContextOptions<InventoryDbContext> _options = null!;
    private IConnectionMultiplexer _connection = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

        _options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInventoryNpgsql(_postgres.GetConnectionString())
            .Options;

        _connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());

        await using var context = new InventoryDbContext(_options);
        await context.Database.MigrateAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
    }

    /// <summary>Seeds <paramref name="count"/> available seats and returns their ids.</summary>
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

    /// <summary>Fires one hold per seat at once, each on its own context, and returns every result.</summary>
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

    /// <summary>Counts what the database actually believes, independent of the results.</summary>
    private async Task<int> CountPersistedHoldsAsync()
    {
        await using var context = new InventoryDbContext(_options);

        return await context.Seats.CountAsync(seat =>
            seat.EventId == _eventId
            && seat.HeldByClientId == _clientA
            && seat.Status == SeatStatus.Held);
    }

    /// <summary>
    /// A burst of twelve ends with at most the cap. Lock losers are refused, so this proves a
    /// ceiling; the next test proves a retrying client reaches it.
    /// </summary>
    [Fact]
    public async Task Hold_WhenOneClientRacesThemselves_ShouldNotExceedTheCap()
    {
        var seatIds = await SeedAvailableSeatsAsync(ConcurrentAttempts);

        var results = await RaceForSeatsAsync(seatIds, new RedisDistributedLock(_connection, NullLogger<RedisDistributedLock>.Instance));
        var held = results.Count(result => result.Outcome is HoldSeatOutcome.Held);

        Assert.True(
            held <= HoldSeatCommandHandler.MaxHoldsPerClientPerEvent,
            $"Client held {held} seats; the cap is {HoldSeatCommandHandler.MaxHoldsPerClientPerEvent}. "
            + $"Outcomes: {Describe(results)}");

        // No refusal indicates a real fault.
        Assert.All(results, result => Assert.Contains(result.Outcome, new[]
        {
            HoldSeatOutcome.Held,
            HoldSeatOutcome.HoldCapReached,
            HoldSeatOutcome.ConcurrentRequestInFlight
        }));

        Assert.Equal(held, await CountPersistedHoldsAsync());
    }

    /// <summary>A client that retries on contention converges on exactly the cap, and stops.</summary>
    [Fact]
    public async Task Hold_WhenOneClientRetriesOnContention_ShouldReachExactlyTheCap()
    {
        var seatIds = await SeedAvailableSeatsAsync(ConcurrentAttempts);
        var distributedLock = new RedisDistributedLock(_connection, NullLogger<RedisDistributedLock>.Instance);

        await using var context = new InventoryDbContext(_options);
        var handler = new HoldSeatCommandHandler(
            new EfSeatRepository(context), distributedLock, TimeProvider.System);

        var held = 0;

        foreach (var seatId in seatIds)
        {
            // Sequential with a bounded retry, as a client would on ConcurrentRequestInFlight.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var result = await handler.HandleAsync(new HoldSeatCommand(_eventId, seatId, _clientA));

                if (result.Outcome is HoldSeatOutcome.Held)
                {
                    held++;
                    break;
                }

                if (result.Outcome is HoldSeatOutcome.HoldCapReached)
                {
                    break;
                }
            }
        }

        Assert.Equal(HoldSeatCommandHandler.MaxHoldsPerClientPerEvent, held);
        Assert.Equal(HoldSeatCommandHandler.MaxHoldsPerClientPerEvent, await CountPersistedHoldsAsync());
    }

    private static string Describe(IReadOnlyList<HoldSeatResult> results) =>
        string.Join(", ", results
            .GroupBy(result => result.Outcome)
            .Select(group => $"{group.Key}={group.Count()}"));
}
