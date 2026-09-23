using Encore.Modules.Inventory.Application;
using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.UnitTests;

/// <summary>
/// The checkout use case, driven through fake ports. The case worth reading first
/// is <see cref="Handle_WhenSeatAlreadySoldToThisClient_ShouldReturnSold"/>: a
/// retried checkout is a success, and the only reason the handler can tell that
/// from somebody else's purchase is that <see cref="Seat"/> keeps the buyer's id
/// on the row after it sells.
/// </summary>
public class SellSeatCommandHandlerTests
{
    private static readonly Guid SeatId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EventId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ClientA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ClientB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Inside the 5-minute hold taken at <see cref="T0"/>.</summary>
    private static readonly DateTime WithinHold = T0.AddMinutes(4);

    private static SellSeatCommand Command => new(EventId, SeatId, ClientA);

    private static Seat AvailableSeat() => Seat.Create(SeatId, EventId);

    private static Seat SeatHeldBy(Guid clientId)
    {
        var seat = AvailableSeat();
        seat.Hold(clientId, T0);
        seat.ClearDomainEvents();
        return seat;
    }

    private static Seat SeatSoldTo(Guid clientId)
    {
        var seat = SeatHeldBy(clientId);
        seat.Sell(clientId, WithinHold);
        seat.ClearDomainEvents();
        return seat;
    }

    private static Seat AnotherSeatHeldBy(Guid clientId, DateTime heldAt) =>
        HeldBy(Seat.Create(Guid.NewGuid(), EventId), clientId, heldAt);

    private static Seat HeldBy(Seat seat, Guid clientId, DateTime heldAt)
    {
        seat.Hold(clientId, heldAt);
        seat.ClearDomainEvents();
        return seat;
    }

    private static SellSeatsCommand BatchOf(params Seat[] seats) =>
        new(EventId, [.. seats.Select(seat => seat.Id)], ClientA);

    private static SellSeatCommandHandler HandlerFor(FakeSeatRepository seats, DateTime? now = null) =>
        new(seats, new FixedTimeProvider(now ?? WithinHold));

    // -- Happy path -------------------------------------------------------

    [Fact]
    public async Task Handle_WhenHoldIsLive_ShouldReturnSold()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientA));

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(SellSeatOutcome.Sold, result.Outcome);
    }

    [Fact]
    public async Task Handle_WhenHoldIsLive_ShouldPersistTheSale()
    {
        var seat = SeatHeldBy(ClientA);
        var seats = new FakeSeatRepository(seat);

        await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(1, seats.SaveCalls);
        Assert.Equal(SeatStatus.Sold, seat.Status);
        Assert.Equal(ClientA, seat.HeldByClientId);
    }

    [Fact]
    public async Task Handle_WhenSaleCompletes_ShouldRaiseSeatSold()
    {
        var seat = SeatHeldBy(ClientA);
        var seats = new FakeSeatRepository(seat);

        await HandlerFor(seats).HandleAsync(Command);

        var sold = Assert.IsType<Domain.Events.SeatSold>(Assert.Single(seat.DomainEvents));
        Assert.Equal(ClientA, sold.ClientId);
    }

    // -- Idempotency for the buyer ----------------------------------------

    /// <summary>
    /// A retried or double-submitted checkout. The purchase already went through,
    /// so reporting failure would be untrue.
    /// </summary>
    [Fact]
    public async Task Handle_WhenSeatAlreadySoldToThisClient_ShouldReturnSold()
    {
        var seats = new FakeSeatRepository(SeatSoldTo(ClientA));

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(SellSeatOutcome.Sold, result.Outcome);
    }

    [Fact]
    public async Task Handle_WhenSeatAlreadySoldToThisClient_ShouldNotWriteAgain()
    {
        var seats = new FakeSeatRepository(SeatSoldTo(ClientA));

        await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(0, seats.SaveCalls);
    }

    [Fact]
    public async Task Handle_WhenSeatAlreadySoldToThisClient_ShouldRaiseNoSecondSeatSold()
    {
        var seat = SeatSoldTo(ClientA);
        var seats = new FakeSeatRepository(seat);

        await HandlerFor(seats).HandleAsync(Command);

        Assert.Empty(seat.DomainEvents);
    }

    /// <summary>The distinction the idempotency rests on: sold, but not to you.</summary>
    [Fact]
    public async Task Handle_WhenSeatSoldToSomebodyElse_ShouldReturnAlreadySold()
    {
        var seats = new FakeSeatRepository(SeatSoldTo(ClientB));

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(SellSeatOutcome.AlreadySold, result.Outcome);
    }

    // -- Refusals ---------------------------------------------------------

    [Fact]
    public async Task Handle_WhenSomebodyElseHoldsTheSeat_ShouldReturnNotTheHolder()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientB));

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(SellSeatOutcome.NotTheHolder, result.Outcome);
    }

    [Fact]
    public async Task Handle_WhenOwnHoldHasLapsed_ShouldReturnHoldExpired()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientA));

        var result = await HandlerFor(seats, now: T0.AddMinutes(6)).HandleAsync(Command);

        Assert.Equal(SellSeatOutcome.HoldExpired, result.Outcome);
    }

    [Fact]
    public async Task Handle_WhenNobodyHoldsTheSeat_ShouldReturnNoActiveHold()
    {
        var seats = new FakeSeatRepository(AvailableSeat());

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(SellSeatOutcome.NoActiveHold, result.Outcome);
    }

    [Fact]
    public async Task Handle_WhenRefused_ShouldNotPersist()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientB));

        await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(0, seats.SaveCalls);
    }

    [Fact]
    public async Task Handle_WhenSeatDoesNotExist_ShouldReturnSeatNotFound()
    {
        var seats = new FakeSeatRepository([null]);

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(SellSeatOutcome.SeatNotFound, result.Outcome);
    }

    /// <summary>
    /// The event id on the command is checked against the seat, so a seat cannot
    /// be bought through another event's route — and the refusal is
    /// indistinguishable from "no such seat", so nobody can use this to discover
    /// which seat ids exist.
    /// </summary>
    [Fact]
    public async Task Handle_WhenSeatBelongsToADifferentEvent_ShouldReportNotFound()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientA));
        var wrongEvent = new SellSeatCommand(Guid.NewGuid(), SeatId, ClientA);

        var result = await HandlerFor(seats).HandleAsync(wrongEvent);

        Assert.Equal(SellSeatOutcome.SeatNotFound, result.Outcome);
        Assert.Equal(0, seats.SaveCalls);
    }

    // -- Lost race, retried once ------------------------------------------

    [Fact]
    public async Task Handle_WhenFirstWriteLosesRace_ShouldReloadBeforeRetrying()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientA), SeatHeldBy(ClientA))
            .WithSaveOutcomes(new ConcurrentSeatModificationException(SeatId), null);

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(SellSeatOutcome.Sold, result.Outcome);
        Assert.Equal(2, seats.GetByIdCalls);
    }

    /// <summary>
    /// The two rules meeting: this client lost the write race against their own
    /// concurrent checkout, and the reload shows the seat already theirs. The
    /// answer is success, not a race-loss the customer cannot act on.
    /// </summary>
    [Fact]
    public async Task Handle_WhenReloadShowsTheClientAlreadyBoughtIt_ShouldReturnSold()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientA), SeatSoldTo(ClientA))
            .WithSaveOutcomes(new ConcurrentSeatModificationException(SeatId));

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(SellSeatOutcome.Sold, result.Outcome);
    }

    [Fact]
    public async Task Handle_WhenReloadShowsSomebodyElseBoughtIt_ShouldReturnAlreadySold()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientA), SeatSoldTo(ClientB))
            .WithSaveOutcomes(new ConcurrentSeatModificationException(SeatId));

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(SellSeatOutcome.AlreadySold, result.Outcome);
    }

    [Fact]
    public async Task Handle_WhenBothAttemptsLoseRace_ShouldReturnLostRace()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientA), SeatHeldBy(ClientA))
            .WithSaveOutcomes(
                new ConcurrentSeatModificationException(SeatId),
                new ConcurrentSeatModificationException(SeatId));

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(SellSeatOutcome.LostRace, result.Outcome);
    }

    [Fact]
    public async Task Handle_WhenContentionPersists_ShouldNotRetryMoreThanOnce()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientA), SeatHeldBy(ClientA), SeatHeldBy(ClientA))
            .WithSaveOutcomes(
                new ConcurrentSeatModificationException(SeatId),
                new ConcurrentSeatModificationException(SeatId),
                null);

        await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(2, seats.SaveCalls);
    }

    // -- All or none (DECISIONS 076) ---------------------------------------

    [Fact]
    public async Task HandleBatch_WhenEveryHoldIsLive_ShouldSellThemAllInOneSave()
    {
        var batch = new[] { AnotherSeatHeldBy(ClientA, T0), AnotherSeatHeldBy(ClientA, T0) };
        var seats = FakeSeatRepository.Holding(batch);

        var result = await HandlerFor(seats).HandleAsync(BatchOf(batch));

        Assert.True(result.AllSold);
        Assert.Equal(1, seats.SaveCalls);
        Assert.Equal(2, seats.LastSaved.Count);
        Assert.All(batch, seat => Assert.Equal(SeatStatus.Sold, seat.Status));
    }

    /// <summary>
    /// The case 028 left for a person to look at. Four seats sold one at a time
    /// and the last hold had lapsed: three stayed sold and the customer was
    /// refunded for them. Asked together, a lapsed hold means nothing sells, and
    /// the seats that did sell in memory are read again so no later save on this
    /// unit of work can write them.
    /// </summary>
    [Fact]
    public async Task HandleBatch_WhenOneHoldHasLapsed_ShouldSellNone()
    {
        var live = AnotherSeatHeldBy(ClientA, T0);
        var lapsed = AnotherSeatHeldBy(ClientA, T0.AddMinutes(-2));
        var seats = FakeSeatRepository.Holding(live, lapsed);

        var result = await HandlerFor(seats).HandleAsync(BatchOf(live, lapsed));

        Assert.False(result.AllSold);
        Assert.Equal([new SeatSaleRefusal(lapsed.Id, SellSeatOutcome.HoldExpired)], result.Refusals);
        Assert.Equal(0, seats.SaveCalls);
        Assert.Equal(2, seats.GetByIdCalls);
    }

    /// <summary>Every seat is asked, so the caller learns every reason at once.</summary>
    [Fact]
    public async Task HandleBatch_WhenSeveralSeatsRefuse_ShouldReportEveryOne()
    {
        var lapsed = AnotherSeatHeldBy(ClientA, T0.AddMinutes(-2));
        var live = AnotherSeatHeldBy(ClientA, T0);
        var theirs = AnotherSeatHeldBy(ClientB, T0);
        var seats = FakeSeatRepository.Holding(lapsed, live, theirs);

        var result = await HandlerFor(seats).HandleAsync(BatchOf(lapsed, live, theirs));

        Assert.Equal(
            [
                new SeatSaleRefusal(lapsed.Id, SellSeatOutcome.HoldExpired),
                new SeatSaleRefusal(theirs.Id, SellSeatOutcome.NotTheHolder)
            ],
            result.Refusals);
    }

    /// <summary>
    /// A retried confirm after the sale went through. Already theirs is not a
    /// refusal, and there is nothing to write.
    /// </summary>
    [Fact]
    public async Task HandleBatch_WhenEverySeatIsAlreadyTheirs_ShouldSucceedWithoutWriting()
    {
        var first = AnotherSeatHeldBy(ClientA, T0);
        var second = AnotherSeatHeldBy(ClientA, T0);
        first.Sell(ClientA, WithinHold);
        second.Sell(ClientA, WithinHold);
        first.ClearDomainEvents();
        second.ClearDomainEvents();

        var seats = FakeSeatRepository.Holding(first, second);

        var result = await HandlerFor(seats).HandleAsync(BatchOf(first, second));

        Assert.True(result.AllSold);
        Assert.Equal(0, seats.SaveCalls);
    }

    [Fact]
    public async Task HandleBatch_WhenOneSeatIsMissing_ShouldSellNone()
    {
        var live = AnotherSeatHeldBy(ClientA, T0);
        var missing = Guid.NewGuid();
        var seats = FakeSeatRepository.Holding(live);

        var result = await HandlerFor(seats)
            .HandleAsync(new SellSeatsCommand(EventId, [live.Id, missing], ClientA));

        Assert.Equal([new SeatSaleRefusal(missing, SellSeatOutcome.SeatNotFound)], result.Refusals);
        Assert.Equal(0, seats.SaveCalls);
    }

    /// <summary>
    /// A lost race rejects the whole write, and the retry is where the batch
    /// learns what happened: here somebody else bought one of the seats, so the
    /// second attempt refuses and writes nothing either.
    /// </summary>
    [Fact]
    public async Task HandleBatch_WhenTheRetryFindsASeatGone_ShouldSellNone()
    {
        var mine = AnotherSeatHeldBy(ClientA, T0);
        var contested = AnotherSeatHeldBy(ClientA, T0);

        var boughtByB = HeldBy(Seat.Create(contested.Id, EventId), ClientB, T0);
        boughtByB.Sell(ClientB, WithinHold);
        boughtByB.ClearDomainEvents();

        var seats = FakeSeatRepository
            .Loading([mine, contested], [HeldBy(Seat.Create(mine.Id, EventId), ClientA, T0), boughtByB])
            .WithSaveOutcomes(new ConcurrentSeatModificationException(contested.Id));

        var result = await HandlerFor(seats).HandleAsync(BatchOf(mine, contested));

        Assert.Equal([new SeatSaleRefusal(contested.Id, SellSeatOutcome.AlreadySold)], result.Refusals);
        Assert.Equal(1, seats.SaveCalls);
    }

    [Fact]
    public async Task HandleBatch_WhenBothAttemptsLoseTheRace_ShouldReportLostRace()
    {
        var batch = new[] { AnotherSeatHeldBy(ClientA, T0), AnotherSeatHeldBy(ClientA, T0) };
        var retried = batch.Select(seat => HeldBy(Seat.Create(seat.Id, EventId), ClientA, T0)).ToArray();

        var seats = FakeSeatRepository.Loading(batch, retried)
            .WithSaveOutcomes(
                new ConcurrentSeatModificationException(batch[1].Id),
                new ConcurrentSeatModificationException(batch[1].Id));

        var result = await HandlerFor(seats).HandleAsync(BatchOf(batch));

        Assert.Equal([new SeatSaleRefusal(batch[1].Id, SellSeatOutcome.LostRace)], result.Refusals);
        Assert.Equal(2, seats.SaveCalls);
    }

    [Fact]
    public async Task HandleBatch_WithASeatNamedTwice_ShouldThrow()
    {
        var seat = AnotherSeatHeldBy(ClientA, T0);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            HandlerFor(FakeSeatRepository.Holding(seat))
                .HandleAsync(new SellSeatsCommand(EventId, [seat.Id, seat.Id], ClientA)));
    }

    // -- Fakes ------------------------------------------------------------

    /// <summary>
    /// Answers each load from a script: one entry per call, the last repeated.
    /// The params constructor scripts one seat per call, which is every
    /// single-seat test; <see cref="Holding"/> and <see cref="Loading"/> script
    /// whole batches.
    /// </summary>
    private sealed class FakeSeatRepository : ISeatRepository
    {
        private readonly IReadOnlyList<IReadOnlyList<Seat>> _loads;
        private readonly List<Exception?> _saveOutcomes = [];

        public FakeSeatRepository(params Seat?[] loads) => _loads = ScriptOfSingles(loads);

        private FakeSeatRepository(IReadOnlyList<IReadOnlyList<Seat>> loads, bool scripted) => _loads = loads;

        public int GetByIdCalls { get; private set; }

        public int SaveCalls { get; private set; }

        /// <summary>The seats handed to the most recent save.</summary>
        public IReadOnlyCollection<Seat> LastSaved { get; private set; } = [];

        /// <summary>Every call returns these seats.</summary>
        public static FakeSeatRepository Holding(params Seat[] seats) =>
            new(new IReadOnlyList<Seat>[] { seats }, scripted: true);

        /// <summary>One batch per call, the last repeated.</summary>
        public static FakeSeatRepository Loading(params IReadOnlyList<Seat>[] loads) =>
            new(loads, scripted: true);

        /// <summary>One entry per expected save: an exception to throw, or null to succeed.</summary>
        public FakeSeatRepository WithSaveOutcomes(params Exception?[] outcomes)
        {
            _saveOutcomes.AddRange(outcomes);
            return this;
        }

        public Task<Seat?> GetByIdAsync(Guid seatId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The handlers load seats in batches.");

        public Task<IReadOnlyList<Seat>> GetByIdsAsync(
            IReadOnlyCollection<Guid> seatIds,
            CancellationToken cancellationToken = default)
        {
            var load = _loads[Math.Min(GetByIdCalls, _loads.Count - 1)];
            GetByIdCalls++;

            return Task.FromResult<IReadOnlyList<Seat>>([.. load.Where(seat => seatIds.Contains(seat.Id))]);
        }

        public Task SaveAsync(Seat seat, CancellationToken cancellationToken = default) =>
            SaveAsync([seat], cancellationToken);

        public Task SaveAsync(IReadOnlyCollection<Seat> seats, CancellationToken cancellationToken = default)
        {
            var outcome = SaveCalls < _saveOutcomes.Count ? _saveOutcomes[SaveCalls] : null;
            SaveCalls++;
            LastSaved = seats;

            return outcome is null ? Task.CompletedTask : Task.FromException(outcome);
        }

        /// <summary>
        /// Never called on this path: the hold cap counts holds, and selling one
        /// releases capacity rather than consuming it. Throwing rather than
        /// returning nothing keeps that a fact the tests would catch changing.
        /// </summary>
        public Task<IReadOnlyCollection<Guid>> FindLiveHoldsAsync(
            Guid clientId,
            Guid eventId,
            DateTime utcNow,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Selling does not consult the hold cap.");

        /// <summary>
        /// The sweep's query, and no handler makes it. Throwing rather than
        /// returning an empty list, so a handler that quietly grew a dependency on
        /// the sweep's candidate list fails a test instead of passing one.
        /// </summary>
        public Task<IReadOnlyList<Guid>> FindExpiredHoldsAsync(
            DateTime utcNow,
            int limit,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The request path does not sweep expired holds.");

        /// <summary>Seats already exist on this path; creating them is a different use case.</summary>
        public Task AddRangeAsync(
            IReadOnlyCollection<Seat> seats,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This use case does not create seats.");

        private static IReadOnlyList<IReadOnlyList<Seat>> ScriptOfSingles(Seat?[] loads)
        {
            var script = loads.Length == 0 ? new Seat?[] { null } : loads;

            return [.. script.Select(seat => seat is null ? Array.Empty<Seat>() : new[] { seat })];
        }
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
