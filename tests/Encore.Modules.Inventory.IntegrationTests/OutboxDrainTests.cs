using System.Diagnostics;
using System.Text.Json;
using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Contracts.Events;
using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>
/// Every event a seat raises becomes exactly one outbox row, in the seat's transaction (015).
/// One test reads the whole outbox, so the database is emptied before each test.
/// </summary>
public sealed class OutboxDrainTests(InventoryDatabase database)
    : IClassFixture<InventoryDatabase>, IAsyncLifetime
{
    private readonly InventoryDatabase _database = database;

    private readonly Guid _eventId = Guid.NewGuid();

    private readonly DbContextOptions<InventoryDbContext> _options = database.Options;

    public Task InitializeAsync() => _database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Hold_ShouldWriteTheRaisedEventAsAnOutboxRow()
    {
        var seatId = await SeedAvailableAsync();
        var clientId = Guid.NewGuid();
        var utcNow = Now();

        await using (var context = new InventoryDbContext(_options))
        {
            var seats = new EfSeatRepository(context);
            var seat = await seats.GetByIdAsync(seatId);

            seat!.Hold(clientId, utcNow);
            await seats.SaveAsync(seat);
        }

        var messages = await MessagesForAsync(seatId);

        var message = Assert.Single(messages);
        Assert.Equal(InventoryEventTypes.SeatHeld, message.EventType);
        Assert.Null(message.ProcessedAt);
        Assert.Equal(0, message.Attempts);
        Assert.NotEqual(Guid.Empty, message.MessageId);

        var payload = JsonSerializer.Deserialize<SeatHeldV1>(
            message.Payload,
            SeatEventPublication.SerializerOptions);

        Assert.NotNull(payload);
        Assert.Equal(seatId, payload.SeatId);
        Assert.Equal(_eventId, payload.EventId);
        Assert.Equal(clientId, payload.ClientId);
        Assert.Equal(utcNow + Seat.HoldDuration, payload.HoldExpiresAt);
    }

    [Fact]
    public async Task Hold_InsideATracedOperation_ShouldRecordItsTraceParent()
    {
        var seatId = await SeedAvailableAsync();

        using (var operation = new Activity("hold").Start())
        {
            await HoldAsync(seatId);

            var message = Assert.Single(await MessagesForAsync(seatId));
            Assert.Equal(operation.Id, message.TraceParent);
        }
    }

    [Fact]
    public async Task Hold_OutsideATrace_ShouldRecordNoTraceParent()
    {
        var seatId = await SeedAvailableAsync();

        Assert.Null(Activity.Current);
        await HoldAsync(seatId);

        Assert.Null(Assert.Single(await MessagesForAsync(seatId)).TraceParent);
    }

    [Fact]
    public async Task Hold_WhenReclaimingALapsedHold_ShouldWriteReleasedBeforeHeld()
    {
        var seatId = await SeedAvailableAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var utcNow = Now();

        await using (var context = new InventoryDbContext(_options))
        {
            var seats = new EfSeatRepository(context);
            var seat = await seats.GetByIdAsync(seatId);

            seat!.Hold(first, utcNow);
            await seats.SaveAsync(seat);
        }

        var afterExpiry = utcNow + Seat.HoldDuration + TimeSpan.FromSeconds(1);

        await using (var context = new InventoryDbContext(_options))
        {
            var seats = new EfSeatRepository(context);
            var seat = await seats.GetByIdAsync(seatId);

            seat!.Hold(second, afterExpiry);
            await seats.SaveAsync(seat);
        }

        var messages = await MessagesForAsync(seatId);

        Assert.Equal(3, messages.Count);
        Assert.Equal(InventoryEventTypes.SeatHeld, messages[0].EventType);
        Assert.Equal(InventoryEventTypes.SeatReleased, messages[1].EventType);
        Assert.Equal(InventoryEventTypes.SeatHeld, messages[2].EventType);

        // One save's events in raised order: table order, not a delivery promise (024).
        Assert.True(messages[1].Id < messages[2].Id);

        var released = JsonSerializer.Deserialize<SeatReleasedV1>(
            messages[1].Payload,
            SeatEventPublication.SerializerOptions);

        Assert.NotNull(released);
        Assert.Equal(SeatReleasedV1.Expired, released.Reason);
        Assert.Equal(first, released.ClientId);
    }

    /// <summary>
    /// Later saves on one context still track the earlier seats: four rows, not ten, because the
    /// drain clears each seat's events after its save (015).
    /// </summary>
    [Fact]
    public async Task Hold_WhenSeveralSeatsAreHeldOnOneContext_ShouldWriteEachEventExactlyOnce()
    {
        var seatIds = new List<Guid>();

        for (var i = 0; i < 4; i++)
        {
            seatIds.Add(await SeedAvailableAsync());
        }

        var clientId = Guid.NewGuid();
        var utcNow = Now();

        await using (var context = new InventoryDbContext(_options))
        {
            var seats = new EfSeatRepository(context);

            foreach (var seatId in seatIds)
            {
                var seat = await seats.GetByIdAsync(seatId);

                seat!.Hold(clientId, utcNow);
                await seats.SaveAsync(seat);
            }
        }

        await using var assertions = new InventoryDbContext(_options);

        var messages = await assertions.OutboxMessages.AsNoTracking()
            .OrderBy(message => message.Id)
            .ToListAsync();

        Assert.Equal(4, messages.Count);
        Assert.All(messages, message => Assert.Equal(InventoryEventTypes.SeatHeld, message.EventType));

        var payloads = messages
            .Select(message => JsonSerializer.Deserialize<SeatHeldV1>(
                message.Payload,
                SeatEventPublication.SerializerOptions)!)
            .ToList();

        Assert.Equal(seatIds.Order(), payloads.Select(payload => payload.SeatId).Order());
        Assert.Equal(4, messages.Select(message => message.MessageId).Distinct().Count());
    }

    [Fact]
    public async Task Save_WhenTheWriteIsRejected_ShouldWriteNoOutboxRow()
    {
        var seatId = await SeedAvailableAsync();
        var utcNow = Now();

        await using var loser = new InventoryDbContext(_options);
        var loserSeats = new EfSeatRepository(loser);

        var stale = await loserSeats.GetByIdAsync(seatId);

        await BumpSeatAsync(seatId, utcNow);

        var before = await CountAsync();

        stale!.Hold(Guid.NewGuid(), utcNow);
        await Assert.ThrowsAsync<ConcurrentSeatModificationException>(
            () => loserSeats.SaveAsync(stale));

        Assert.Equal(before, await CountAsync());
    }

    [Fact]
    public async Task Save_WhenARejectedAttemptIsRetried_ShouldWriteOnlyTheSuccessfulAttempt()
    {
        var seatId = await SeedAvailableAsync();
        var clientId = Guid.NewGuid();
        var utcNow = Now();

        await using var context = new InventoryDbContext(_options);
        var seats = new EfSeatRepository(context);

        var stale = await seats.GetByIdAsync(seatId);

        await BumpSeatAsync(seatId, utcNow);

        stale!.Hold(clientId, utcNow);
        await Assert.ThrowsAsync<ConcurrentSeatModificationException>(() => seats.SaveAsync(stale));

        // The retry, as a handler performs it: a batch load discards the stale seat and its events.
        var reloaded = Assert.Single(await seats.GetByIdsAsync([seatId]));
        reloaded.Hold(clientId, utcNow);
        await seats.SaveAsync(reloaded);

        var messages = await MessagesForAsync(seatId);

        Assert.Equal(3, messages.Count);

        var held = messages
            .Where(message => message.EventType == InventoryEventTypes.SeatHeld)
            .Select(message => JsonSerializer.Deserialize<SeatHeldV1>(
                message.Payload,
                SeatEventPublication.SerializerOptions)!)
            .ToList();

        Assert.Equal(2, held.Count);
        Assert.Single(held, payload => payload.ClientId == clientId);
    }

    [Fact]
    public async Task Hold_WhenTheSameClientReholds_ShouldWriteNoSecondRow()
    {
        var seatId = await SeedAvailableAsync();
        var clientId = Guid.NewGuid();
        var utcNow = Now();

        await using (var context = new InventoryDbContext(_options))
        {
            var seats = new EfSeatRepository(context);
            var seat = await seats.GetByIdAsync(seatId);

            seat!.Hold(clientId, utcNow);
            await seats.SaveAsync(seat);
        }

        await using (var context = new InventoryDbContext(_options))
        {
            var seats = new EfSeatRepository(context);
            var seat = await seats.GetByIdAsync(seatId);

            seat!.Hold(clientId, utcNow + TimeSpan.FromSeconds(30));
            await seats.SaveAsync(seat);
        }

        Assert.Single(await MessagesForAsync(seatId));
    }

    [Fact]
    public async Task AddRange_ShouldGoThroughTheDrainAndWriteNothingToday()
    {
        var before = await CountAsync();

        await using var context = new InventoryDbContext(_options);
        var seats = new EfSeatRepository(context);

        await seats.AddRangeAsync(
        [
            Seat.Create(Guid.NewGuid(), _eventId),
            Seat.Create(Guid.NewGuid(), _eventId)
        ]);

        Assert.Equal(before, await CountAsync());
    }

    [Fact]
    public async Task Save_WhenSomethingElseAddedAnOutboxRow_ShouldNotDiscardIt()
    {
        var seatId = await SeedAvailableAsync();
        var utcNow = Now();

        await using var context = new InventoryDbContext(_options);

        var byHand = OutboxMessage.For(
            Guid.CreateVersion7(),
            InventoryEventTypes.SeatSold,
            """{"note":"added by something other than the drain"}""",
            utcNow);

        context.OutboxMessages.Add(byHand);

        var seats = new EfSeatRepository(context);
        var seat = await seats.GetByIdAsync(seatId);

        seat!.Hold(Guid.NewGuid(), utcNow);
        await seats.SaveAsync(seat);

        await using var assertions = new InventoryDbContext(_options);

        Assert.True(
            await assertions.OutboxMessages.AnyAsync(message => message.MessageId == byHand.MessageId),
            "the hand-added row was discarded by the drain");
    }

    [Fact]
    public async Task Sell_ShouldPublishTheBuyer()
    {
        var seatId = await SeedAvailableAsync();
        var clientId = Guid.NewGuid();
        var utcNow = Now();

        await using (var context = new InventoryDbContext(_options))
        {
            var seats = new EfSeatRepository(context);
            var seat = await seats.GetByIdAsync(seatId);

            seat!.Hold(clientId, utcNow);
            seat.Sell(clientId, utcNow);
            await seats.SaveAsync(seat);
        }

        var messages = await MessagesForAsync(seatId);

        Assert.Equal(2, messages.Count);
        Assert.Equal(InventoryEventTypes.SeatSold, messages[1].EventType);

        var sold = JsonSerializer.Deserialize<SeatSoldV1>(
            messages[1].Payload,
            SeatEventPublication.SerializerOptions);

        Assert.NotNull(sold);
        Assert.Equal(seatId, sold.SeatId);
        Assert.Equal(clientId, sold.ClientId);
    }

    private async Task<Guid> SeedAvailableAsync()
    {
        var seatId = Guid.NewGuid();

        await using var context = new InventoryDbContext(_options);

        context.Seats.Add(Seat.Create(seatId, _eventId));
        await context.SaveChangesAsync();

        return seatId;
    }

    private async Task HoldAsync(Guid seatId)
    {
        await using var context = new InventoryDbContext(_options);
        var seats = new EfSeatRepository(context);

        var seat = await seats.GetByIdAsync(seatId);

        seat!.Hold(Guid.NewGuid(), Now());
        await seats.SaveAsync(seat);
    }

    private async Task BumpSeatAsync(Guid seatId, DateTime utcNow)
    {
        var other = Guid.NewGuid();

        await using var context = new InventoryDbContext(_options);
        var seats = new EfSeatRepository(context);

        var seat = await seats.GetByIdAsync(seatId);

        seat!.Hold(other, utcNow);
        await seats.SaveAsync(seat);

        seat.Release(other, utcNow);
        await seats.SaveAsync(seat);
    }

    private async Task<List<OutboxMessage>> MessagesForAsync(Guid seatId)
    {
        await using var context = new InventoryDbContext(_options);

        return await context.OutboxMessages.AsNoTracking()
            .Where(message => EF.Functions.JsonContains(
                message.Payload,
                $$"""{"seatId":"{{seatId}}"}"""))
            .OrderBy(message => message.Id)
            .ToListAsync();
    }

    private async Task<int> CountAsync()
    {
        await using var context = new InventoryDbContext(_options);

        return await context.OutboxMessages.CountAsync();
    }

    // Truncated to microseconds to survive the Postgres round trip.
    private static DateTime Now()
    {
        var utcNow = DateTime.UtcNow;

        return new DateTime(utcNow.Ticks - utcNow.Ticks % TimeSpan.TicksPerMicrosecond, DateTimeKind.Utc);
    }
}
