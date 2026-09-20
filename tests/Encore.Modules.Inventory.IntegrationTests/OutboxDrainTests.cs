using System.Text.Json;
using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Contracts.Events;
using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>
/// The drain: every domain event a seat raises becomes an outbox row, in the same
/// transaction as the seat, exactly once.
/// </summary>
/// <remarks>
/// <para>
/// Integration rather than unit tests, because the thing under test is a
/// <c>SaveChanges</c> override and there is no honest way to exercise one without a
/// database. An in-memory provider would not do: the claims here are about what
/// lands in Postgres and what does not when a transaction is rejected.
/// </para>
/// <para>
/// No dispatcher anywhere in this file. Writing the row and delivering it are
/// separate halves with separate failure modes, and only the first is atomic with
/// the seat — keeping them apart in the tests is the same discipline as keeping
/// them apart in the code.
/// </para>
/// </remarks>
public sealed class OutboxDrainTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("encore")
        .WithUsername("encore")
        .WithPassword("encore")
        .Build();

    private readonly Guid _eventId = Guid.NewGuid();

    private DbContextOptions<InventoryDbContext> _options = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        _options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInventoryNpgsql(_postgres.GetConnectionString())
            .Options;

        await using var context = new InventoryDbContext(_options);

        // Migrate rather than EnsureCreated, so this also proves the generated
        // migration applies against real Postgres — including the partial index,
        // whose filter is a SQL literal no compiler checks.
        await context.Database.MigrateAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task Hold_ShouldWriteTheRaisedEventAsAnOutboxRow()
    {
        var seatId = await SeatAsync();
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

        // The payload is the published contract, not the domain record. If someone
        // switches the drain to serialise SeatHeld directly this still passes on
        // three fields and fails on the fourth, because the domain record has no
        // notion of a version and names its reason differently.
        var payload = JsonSerializer.Deserialize<SeatHeldV1>(
            message.Payload,
            SeatEventPublication.SerializerOptions);

        Assert.NotNull(payload);
        Assert.Equal(seatId, payload.SeatId);
        Assert.Equal(_eventId, payload.EventId);
        Assert.Equal(clientId, payload.ClientId);
        Assert.Equal(utcNow + Seat.HoldDuration, payload.HoldExpiresAt);
    }

    /// <summary>
    /// The ordering guarantee 007 depends on: a reclaim ends the old hold in the log
    /// before it starts the new one, so hold history stays reconstructable.
    /// </summary>
    /// <remarks>
    /// One save raises both events, so this is the case the sequence really does
    /// promise — the ids are adjacent and assigned in the order the aggregate raised
    /// them. Across transactions there is no such promise, which is why this test
    /// deliberately drives both events through a single write.
    /// </remarks>
    [Fact]
    public async Task Hold_WhenReclaimingALapsedHold_ShouldWriteReleasedBeforeHeld()
    {
        var seatId = await SeatAsync();
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

        // Past the five-minute window, so the next hold reclaims rather than refuses.
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

        // Ascending ids, so a consumer reading in id order sees the reclaim in the
        // order it happened.
        Assert.True(messages[1].Id < messages[2].Id);

        var released = JsonSerializer.Deserialize<SeatReleasedV1>(
            messages[1].Payload,
            SeatEventPublication.SerializerOptions);

        Assert.NotNull(released);
        Assert.Equal(SeatReleasedV1.Expired, released.Reason);
        Assert.Equal(first, released.ClientId);
    }

    /// <summary>
    /// The bug this whole design exists to prevent, and the reason 044's open
    /// question about clearing had to be answered "yes".
    /// </summary>
    /// <remarks>
    /// A four-seat checkout drives four holds through one scoped context. Each
    /// handler clears only the seat it is about to transition, so a seat that kept
    /// its events after its own save would have them drained again by every save
    /// that followed — four rows for the first seat's hold, three for the second,
    /// and so on. Delete the <c>ClearDomainEvents</c> call in
    /// <c>InventoryDbContext</c> and this test reports ten rows instead of four.
    /// </remarks>
    [Fact]
    public async Task Hold_WhenSeveralSeatsAreHeldOnOneContext_ShouldWriteEachEventExactlyOnce()
    {
        var seatIds = new List<Guid>();

        for (var i = 0; i < 4; i++)
        {
            seatIds.Add(await SeatAsync());
        }

        var clientId = Guid.NewGuid();
        var utcNow = Now();

        // One context for all four, exactly as a checkout has.
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

        // One per seat, and four distinct message ids rather than one repeated.
        var payloads = messages
            .Select(message => JsonSerializer.Deserialize<SeatHeldV1>(
                message.Payload,
                SeatEventPublication.SerializerOptions)!)
            .ToList();

        Assert.Equal(seatIds.Order(), payloads.Select(payload => payload.SeatId).Order());
        Assert.Equal(4, messages.Select(message => message.MessageId).Distinct().Count());
    }

    /// <summary>
    /// A rejected save writes nothing at all — which is the single guarantee an
    /// outbox exists to make.
    /// </summary>
    [Fact]
    public async Task Save_WhenTheWriteIsRejected_ShouldWriteNoOutboxRow()
    {
        var seatId = await SeatAsync();
        var utcNow = Now();

        await using var loser = new InventoryDbContext(_options);
        var loserSeats = new EfSeatRepository(loser);

        // Loaded before anybody else writes, so it is holding a row version that is
        // about to go stale.
        var stale = await loserSeats.GetByIdAsync(seatId);

        await BumpSeatAsync(seatId, utcNow);

        var before = await CountAsync();

        stale!.Hold(Guid.NewGuid(), utcNow);
        await Assert.ThrowsAsync<ConcurrentSeatModificationException>(
            () => loserSeats.SaveAsync(stale));

        Assert.Equal(before, await CountAsync());
    }

    /// <summary>
    /// 044's named hazard: a rejected save leaves its outbox rows tracked as Added,
    /// and all three seat handlers retry once. The attempt that succeeds must not
    /// carry the rejected attempt's events along with its own.
    /// </summary>
    [Fact]
    public async Task Save_WhenARejectedAttemptIsRetried_ShouldWriteOnlyTheSuccessfulAttempt()
    {
        var seatId = await SeatAsync();
        var clientId = Guid.NewGuid();
        var utcNow = Now();

        await using var context = new InventoryDbContext(_options);
        var seats = new EfSeatRepository(context);

        var stale = await seats.GetByIdAsync(seatId);

        // Somebody else holds and releases it, so the row moves on twice and comes
        // back Available — the loser's retry can then succeed.
        await BumpSeatAsync(seatId, utcNow);

        stale!.Hold(clientId, utcNow);
        await Assert.ThrowsAsync<ConcurrentSeatModificationException>(() => seats.SaveAsync(stale));

        // The retry, exactly as a handler performs it: reload, scrub, try again.
        var reloaded = await seats.GetByIdAsync(seatId);
        reloaded!.ClearDomainEvents();
        reloaded.Hold(clientId, utcNow);
        await seats.SaveAsync(reloaded);

        var messages = await MessagesForAsync(seatId);

        // The other writer's hold and release, then one hold from the retry. Three,
        // not four: the rejected attempt contributed nothing.
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

    /// <summary>
    /// Re-holding a seat you already hold raises nothing, so it publishes nothing.
    /// </summary>
    /// <remarks>
    /// The idempotent path in <c>Seat.Hold</c> returns without touching state (007).
    /// If it ever started raising an event, a client retrying a dropped response
    /// would publish a second hold for a hold that never moved — so this pins the
    /// silence rather than the behaviour.
    /// </remarks>
    [Fact]
    public async Task Hold_WhenTheSameClientReholds_ShouldWriteNoSecondRow()
    {
        var seatId = await SeatAsync();
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

    /// <summary>
    /// The bulk-creation path drains too, and today that means it writes nothing.
    /// </summary>
    /// <remarks>
    /// <c>Seat.Create</c> raises no events, so this asserts zero. That is a fact
    /// about this week's domain rather than a guarantee, which is exactly why the
    /// path is pinned: <c>EfSeatRepository</c> calls <c>SaveChangesAsync</c> from
    /// <c>AddRangeAsync</c> as well as from <c>SaveAsync</c>, and 044 records that a
    /// drain written in the repository would have covered only one of them. The day
    /// creation raises something, this test is where the change shows up.
    /// </remarks>
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

    /// <summary>
    /// The drain discards the leavings of its own rejected save, and nobody else's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This pins a trap rather than a symptom. The first version of
    /// <c>DiscardRejectedOutboxRows</c> detached <em>every</em> outbox row tracked as
    /// <c>Added</c>, reasoning that a committed row is <c>Unchanged</c> so nothing else
    /// could be caught. That is wrong about anybody who adds a row and saves it
    /// themselves — theirs is <c>Added</c> too, and it was silently thrown away.
    /// </para>
    /// <para>
    /// Nothing in production adds an outbox row by hand today, so the bug had no
    /// symptom in the running system at all. It surfaced only because
    /// <c>OutboxDispatcherTests</c> seeds its rows that way and then found nothing to
    /// claim. A test is cheaper than rediscovering that.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Save_WhenSomethingElseAddedAnOutboxRow_ShouldNotDiscardIt()
    {
        var seatId = await SeatAsync();
        var utcNow = Now();

        await using var context = new InventoryDbContext(_options);

        // Added by hand, exactly as a seeder or a reconciliation job would.
        var byHand = OutboxMessage.For(
            Guid.CreateVersion7(),
            InventoryEventTypes.SeatSold,
            """{"note":"added by something other than the drain"}""",
            utcNow);

        context.OutboxMessages.Add(byHand);

        // Saved alongside a transition, so the drain runs in the same breath.
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
        var seatId = await SeatAsync();
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

    /// <summary>Creates a seat and returns its id.</summary>
    private async Task<Guid> SeatAsync()
    {
        var seatId = Guid.NewGuid();

        await using var context = new InventoryDbContext(_options);

        context.Seats.Add(Seat.Create(seatId, _eventId));
        await context.SaveChangesAsync();

        return seatId;
    }

    /// <summary>
    /// Moves the seat on by two row versions through another context, leaving it
    /// Available again and two outbox rows behind.
    /// </summary>
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

        // Filtering in the payload is what the jsonb column is for, and it is the
        // only way to scope these assertions to one seat while the container is
        // shared across every test in this class.
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

    /// <summary>
    /// Truncated to whole microseconds, because Postgres <c>timestamptz</c> resolves
    /// to a microsecond while <see cref="DateTime"/> ticks are 100ns — an untruncated
    /// instant comes back slightly different from what went in, and the payload
    /// assertions here compare instants for equality.
    /// </summary>
    private static DateTime Now()
    {
        var utcNow = DateTime.UtcNow;

        return new DateTime(utcNow.Ticks - (utcNow.Ticks % TimeSpan.TicksPerMicrosecond), DateTimeKind.Utc);
    }
}
