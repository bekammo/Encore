using Encore.Modules.Inventory.Application;
using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.UnitTests;

/// <summary>
/// The hold use case, driven entirely through fake ports. No database, no Redis,
/// no container — which is the whole return on the hexagon: the sequencing logic
/// is testable in microseconds, and the decisions that matter most here (the
/// client lock is refused on contention and waved through on outage, a lost race
/// is retried once, a batch answers every seat) are asserted directly rather
/// than inferred from a load test.
/// </summary>
public class HoldSeatCommandHandlerTests
{
    private static readonly Guid SeatId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EventId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ClientA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ClientB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static HoldSeatCommand Command => new(EventId, SeatId, ClientA);

    private static Seat AvailableSeat() => Seat.Create(SeatId, EventId);

    private static Seat AnotherAvailableSeat() => Seat.Create(Guid.NewGuid(), EventId);

    private static Seat SeatHeldBy(Guid clientId) => HeldBy(AvailableSeat(), clientId);

    private static Seat HeldBy(Seat seat, Guid clientId)
    {
        seat.Hold(clientId, T0);
        seat.ClearDomainEvents();
        return seat;
    }

    private static Seat SoldSeat()
    {
        var seat = SeatHeldBy(ClientA);
        seat.Sell(ClientA, T0);
        seat.ClearDomainEvents();
        return seat;
    }

    private static HoldSeatsCommand BatchOf(params Seat[] seats) =>
        new(EventId, [.. seats.Select(seat => seat.Id)], ClientA);

    private static HoldSeatCommandHandler HandlerFor(
        FakeSeatRepository seats,
        FakeDistributedLock? distributedLock = null) =>
        new(seats, distributedLock ?? new FakeDistributedLock(), new FixedTimeProvider(T0));

    // -- Happy path -------------------------------------------------------

    [Fact]
    public async Task Handle_WhenSeatAvailable_ShouldReturnHeld()
    {
        var seats = new FakeSeatRepository(AvailableSeat());

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(HoldSeatOutcome.Held, result.Outcome);
    }

    [Fact]
    public async Task Handle_WhenSeatAvailable_ShouldReturnExpiryFiveMinutesOut()
    {
        var seats = new FakeSeatRepository(AvailableSeat());

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(T0 + Seat.HoldDuration, result.HoldExpiresAt);
    }

    [Fact]
    public async Task Handle_WhenSeatAvailable_ShouldPersistExactlyOnce()
    {
        var seat = AvailableSeat();
        var seats = new FakeSeatRepository(seat);

        await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(1, seats.SaveCalls);
        Assert.Equal(SeatStatus.Held, seat.Status);
        Assert.Equal(ClientA, seat.HeldByClientId);
    }

    // -- Refusals come back as results, never as exceptions ---------------

    [Fact]
    public async Task Handle_WhenSeatHeldByAnotherClient_ShouldReturnAlreadyHeld()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientB));

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(HoldSeatOutcome.AlreadyHeld, result.Outcome);
        Assert.Null(result.HoldExpiresAt);
    }

    [Fact]
    public async Task Handle_WhenSeatSold_ShouldReturnAlreadySold()
    {
        var seats = new FakeSeatRepository(SoldSeat());

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(HoldSeatOutcome.AlreadySold, result.Outcome);
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
        // [null] rather than null: a bare null would bind as the params array itself.
        var seats = new FakeSeatRepository([null]);

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(HoldSeatOutcome.SeatNotFound, result.Outcome);
        Assert.Equal(0, seats.SaveCalls);
    }

    // -- The lock is an optimisation, and behaves like one -----------------

    /// <summary>
    /// The load-bearing test for the whole locking story. If this ever fails,
    /// Redis has quietly become a correctness dependency and the architecture's
    /// central claim — that it survives Redis being gone — is no longer true.
    /// </summary>
    [Fact]
    public async Task Handle_WhenLockCannotBeAcquired_ShouldStillTakeTheHold()
    {
        var seats = new FakeSeatRepository(AvailableSeat());
        var distributedLock = new FakeDistributedLock(LockOutcome.Unavailable);

        var result = await HandlerFor(seats, distributedLock).HandleAsync(Command);

        Assert.Equal(HoldSeatOutcome.Held, result.Outcome);
        Assert.Equal(1, seats.SaveCalls);
    }

    [Fact]
    public async Task Handle_WhenLockCannotBeAcquired_ShouldNotReleaseSomebodyElsesLock()
    {
        var seats = new FakeSeatRepository(AvailableSeat());
        var distributedLock = new FakeDistributedLock(LockOutcome.Unavailable);

        await HandlerFor(seats, distributedLock).HandleAsync(Command);

        Assert.Equal(0, distributedLock.ReleaseCalls);
    }

    [Fact]
    public async Task Handle_WhenLockAcquired_ShouldReleaseIt()
    {
        var seats = new FakeSeatRepository(AvailableSeat());
        var distributedLock = new FakeDistributedLock();

        await HandlerFor(seats, distributedLock).HandleAsync(Command);

        Assert.Equal(1, distributedLock.ReleaseCalls);
    }

    [Fact]
    public async Task Handle_WhenAttemptRefused_ShouldStillReleaseTheLock()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientB));
        var distributedLock = new FakeDistributedLock();

        await HandlerFor(seats, distributedLock).HandleAsync(Command);

        Assert.Equal(1, distributedLock.ReleaseCalls);
    }

    [Fact]
    public async Task Handle_ShouldLockOnTheClientAndEvent()
    {
        var seats = new FakeSeatRepository(AvailableSeat());
        var distributedLock = new FakeDistributedLock();

        await HandlerFor(seats, distributedLock).HandleAsync(Command);

        Assert.Contains($"client:{ClientA}:event:{EventId}", distributedLock.Acquired);
    }

    /// <summary>
    /// There is no seat lock (076). It was taken on every write and every handler
    /// proceeded whatever it answered, so it excluded nobody and cost two round
    /// trips a hold. The client lock is the only one, and the only one with a
    /// job the row's token cannot do.
    /// </summary>
    [Fact]
    public async Task Handle_ShouldTakeNoLockButTheClientLock()
    {
        var seats = new FakeSeatRepository(AvailableSeat());
        var distributedLock = new FakeDistributedLock();

        await HandlerFor(seats, distributedLock).HandleAsync(Command);

        Assert.Equal([$"client:{ClientA}:event:{EventId}"], distributedLock.Acquired);
    }

    [Fact]
    public async Task Handle_ShouldTakeLockWithATtlSizedToOneWrite()
    {
        var seats = new FakeSeatRepository(AvailableSeat());
        var distributedLock = new FakeDistributedLock();

        await HandlerFor(seats, distributedLock).HandleAsync(Command);

        Assert.InRange(distributedLock.LastTtl, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
    }

    // -- A lost race is retried exactly once ------------------------------

    [Fact]
    public async Task Handle_WhenFirstWriteLosesRace_ShouldReloadBeforeRetrying()
    {
        var seats = new FakeSeatRepository(AvailableSeat(), AvailableSeat())
            .WithSaveOutcomes(new ConcurrentSeatModificationException(SeatId), null);

        await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(2, seats.GetByIdCalls);
        Assert.Equal(2, seats.SaveCalls);
    }

    [Fact]
    public async Task Handle_WhenRetrySucceeds_ShouldReturnHeld()
    {
        var seats = new FakeSeatRepository(AvailableSeat(), AvailableSeat())
            .WithSaveOutcomes(new ConcurrentSeatModificationException(SeatId), null);

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(HoldSeatOutcome.Held, result.Outcome);
    }

    /// <summary>
    /// The reason the retry exists. Losing the race means somebody else wrote
    /// first, so the reload finds their hold and the client gets the truthful
    /// "somebody already has it" instead of a bare race-loss they cannot act on.
    /// </summary>
    [Fact]
    public async Task Handle_WhenReloadRevealsTheWinner_ShouldReturnAlreadyHeld()
    {
        var seats = new FakeSeatRepository(AvailableSeat(), SeatHeldBy(ClientB))
            .WithSaveOutcomes(new ConcurrentSeatModificationException(SeatId));

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(HoldSeatOutcome.AlreadyHeld, result.Outcome);
    }

    [Fact]
    public async Task Handle_WhenBothAttemptsLoseRace_ShouldReturnLostRace()
    {
        var seats = new FakeSeatRepository(AvailableSeat(), AvailableSeat())
            .WithSaveOutcomes(
                new ConcurrentSeatModificationException(SeatId),
                new ConcurrentSeatModificationException(SeatId));

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(HoldSeatOutcome.LostRace, result.Outcome);
    }

    /// <summary>
    /// Bounded at one. Retrying harder under sustained contention is how a
    /// thundering herd gets worse rather than better.
    /// </summary>
    [Fact]
    public async Task Handle_WhenContentionPersists_ShouldNotRetryMoreThanOnce()
    {
        var seats = new FakeSeatRepository(AvailableSeat(), AvailableSeat(), AvailableSeat())
            .WithSaveOutcomes(
                new ConcurrentSeatModificationException(SeatId),
                new ConcurrentSeatModificationException(SeatId),
                null);

        await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(2, seats.SaveCalls);
    }

    /// <summary>
    /// A rejected write still raised its events on the aggregate instance. They
    /// describe something that never happened, so they must not survive into the
    /// attempt that does — otherwise the outbox would later publish a hold that
    /// the database refused.
    /// </summary>
    [Fact]
    public async Task Handle_WhenRetrying_ShouldDropEventsFromTheRejectedAttempt()
    {
        // The same instance comes back from the reload, exactly as the EF adapter
        // returns the instance it re-read in place.
        var seat = AvailableSeat();
        var seats = new FakeSeatRepository(seat, seat)
            .WithSaveOutcomes(new ConcurrentSeatModificationException(SeatId), null);

        await HandlerFor(seats).HandleAsync(Command);

        // The first attempt raised SeatHeld before its write was rejected. The
        // second finds the seat already held by this same client, so re-holding is
        // the idempotent no-op that raises nothing. Empty is therefore the only
        // correct answer: drop the ClearDomainEvents() call from the handler and
        // the phantom SeatHeld from the rejected write is left sitting here.
        Assert.Empty(seat.DomainEvents);
    }

    // -- The per-client hold cap (DECISIONS 006) --------------------------

    [Fact]
    public async Task Handle_WhenClientHoldsFewerThanTheCap_ShouldHold()
    {
        var seats = new FakeSeatRepository(AvailableSeat())
            .WithLiveHolds(HoldSeatCommandHandler.MaxHoldsPerClientPerEvent - 1);

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(HoldSeatOutcome.Held, result.Outcome);
    }

    [Fact]
    public async Task Handle_WhenClientIsAtTheCap_ShouldRefuse()
    {
        var seats = new FakeSeatRepository(AvailableSeat())
            .WithLiveHolds(HoldSeatCommandHandler.MaxHoldsPerClientPerEvent);

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(HoldSeatOutcome.HoldCapReached, result.Outcome);
    }

    [Fact]
    public async Task Handle_WhenCapReached_ShouldNotWrite()
    {
        var seats = new FakeSeatRepository(AvailableSeat())
            .WithLiveHolds(HoldSeatCommandHandler.MaxHoldsPerClientPerEvent);

        await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(0, seats.SaveCalls);
    }

    /// <summary>
    /// The case the cap has to step around. A client holding their full
    /// allowance who re-sends a request for one of those very seats must still be
    /// told yes — it is already true. Counting the requested seat would refuse
    /// them their own seat on exactly the retry path idempotency exists to protect.
    /// </summary>
    [Fact]
    public async Task Handle_WhenAtCapAndReHoldingASeatTheyAlreadyHold_ShouldSucceed()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientA))
            .WithLiveHolds(HoldSeatCommandHandler.MaxHoldsPerClientPerEvent - 1)
            .WithLiveHoldOn(SeatId);

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(HoldSeatOutcome.Held, result.Outcome);
    }

    /// <summary>
    /// The honest half of DECISIONS 006: the cap is a policy enforced
    /// best-effort, not an invariant. With the lock unavailable the handler does
    /// not refuse — it proceeds, because aborting would promote Redis to a
    /// correctness dependency. A breach is a refund email; refusing every hold
    /// because Redis blinked is an outage.
    /// </summary>
    [Fact]
    public async Task Handle_WhenLockUnavailable_ShouldStillEnforceCapOnTheHappyPath()
    {
        var seats = new FakeSeatRepository(AvailableSeat())
            .WithLiveHolds(HoldSeatCommandHandler.MaxHoldsPerClientPerEvent);

        var result = await HandlerFor(seats, new FakeDistributedLock(LockOutcome.Unavailable))
            .HandleAsync(Command);

        // Uncontended, the count is still correct, so the cap still holds. What
        // the missing lock costs is the *race*, which this test cannot show and
        // ConcurrentHoldCapTests does.
        Assert.Equal(HoldSeatOutcome.HoldCapReached, result.Outcome);
    }

    // -- The client lock has no backstop ----------------------------------

    /// <summary>
    /// The client lock has nothing behind it. This is the test whose absence let
    /// twelve concurrent holds past a cap of four: proceeding here is not a
    /// weaker cap, it is no cap, because concurrent requests by one client are
    /// exactly when this lock is contended.
    /// </summary>
    [Fact]
    public async Task Handle_WhenClientLockHeldByAnother_ShouldRefuse()
    {
        var seats = new FakeSeatRepository(AvailableSeat());
        var distributedLock = new FakeDistributedLock(LockOutcome.HeldByAnother);

        var result = await HandlerFor(seats, distributedLock).HandleAsync(Command);

        Assert.Equal(HoldSeatOutcome.ConcurrentRequestInFlight, result.Outcome);
    }

    [Fact]
    public async Task Handle_WhenClientLockHeldByAnother_ShouldNotTouchTheDatabase()
    {
        var seats = new FakeSeatRepository(AvailableSeat());
        var distributedLock = new FakeDistributedLock(LockOutcome.HeldByAnother);

        await HandlerFor(seats, distributedLock).HandleAsync(Command);

        Assert.Equal(0, seats.GetByIdCalls);
        Assert.Equal(0, seats.SaveCalls);
    }

    [Fact]
    public async Task Handle_WhenClientLockHeldByAnother_ShouldReleaseNothing()
    {
        var seats = new FakeSeatRepository(AvailableSeat());
        var distributedLock = new FakeDistributedLock(LockOutcome.HeldByAnother);

        await HandlerFor(seats, distributedLock).HandleAsync(Command);

        Assert.Empty(distributedLock.Acquired);
        Assert.Equal(0, distributedLock.ReleaseCalls);
    }

    /// <summary>
    /// The other half of DECISIONS 006's asymmetry. Unreachable is not contended:
    /// refusing every hold in the system because Redis blinked would be an
    /// outage, where a breached cap is a refund email. So this proceeds, and the
    /// cap becomes best-effort exactly as 006 says it is.
    /// </summary>
    [Fact]
    public async Task Handle_WhenClientLockUnavailable_ShouldProceedAnyway()
    {
        var seats = new FakeSeatRepository(AvailableSeat());
        var distributedLock = new FakeDistributedLock(LockOutcome.Unavailable);

        var result = await HandlerFor(seats, distributedLock).HandleAsync(Command);

        Assert.Equal(HoldSeatOutcome.Held, result.Outcome);
        Assert.Equal(1, seats.SaveCalls);
    }

    // -- The event id is checked, not trusted -----------------------------

    [Fact]
    public async Task Handle_WhenSeatBelongsToADifferentEvent_ShouldReportNotFound()
    {
        var seats = new FakeSeatRepository(AvailableSeat());
        var wrongEvent = new HoldSeatCommand(Guid.NewGuid(), SeatId, ClientA);

        var result = await HandlerFor(seats).HandleAsync(wrongEvent);

        Assert.Equal(HoldSeatOutcome.SeatNotFound, result.Outcome);
    }

    [Fact]
    public async Task Handle_WhenSeatBelongsToADifferentEvent_ShouldNotWrite()
    {
        var seats = new FakeSeatRepository(AvailableSeat());
        var wrongEvent = new HoldSeatCommand(Guid.NewGuid(), SeatId, ClientA);

        await HandlerFor(seats).HandleAsync(wrongEvent);

        Assert.Equal(0, seats.SaveCalls);
    }

    // -- Batches (DECISIONS 076) -------------------------------------------

    /// <summary>
    /// 023, kept by the batch: a refused seat does not cost the client the seats
    /// that could be held, and every seat is answered.
    /// </summary>
    [Fact]
    public async Task HandleBatch_WhenOneSeatIsRefused_ShouldStillHoldTheOthers()
    {
        var first = AnotherAvailableSeat();
        var taken = HeldBy(AnotherAvailableSeat(), ClientB);
        var last = AnotherAvailableSeat();
        var seats = FakeSeatRepository.Holding(first, taken, last);

        var results = await HandlerFor(seats).HandleAsync(BatchOf(first, taken, last));

        Assert.Equal(
            [HoldSeatOutcome.Held, HoldSeatOutcome.AlreadyHeld, HoldSeatOutcome.Held],
            results.Select(result => result.Outcome));
        Assert.Equal(ClientA, first.HeldByClientId);
        Assert.Equal(ClientA, last.HeldByClientId);
    }

    /// <summary>What the batch is for: every hold written by one save, not one each.</summary>
    [Fact]
    public async Task HandleBatch_ShouldWriteEveryHoldInOneSave()
    {
        var batch = new[] { AnotherAvailableSeat(), AnotherAvailableSeat(), AnotherAvailableSeat() };
        var seats = FakeSeatRepository.Holding(batch);

        await HandlerFor(seats).HandleAsync(BatchOf(batch));

        Assert.Equal(1, seats.SaveCalls);
        Assert.Equal(1, seats.GetByIdCalls);
        Assert.Equal(3, seats.LastSaved.Count);
    }

    [Fact]
    public async Task HandleBatch_ShouldTakeTheClientLockOnce()
    {
        var batch = new[] { AnotherAvailableSeat(), AnotherAvailableSeat() };
        var distributedLock = new FakeDistributedLock();

        await HandlerFor(FakeSeatRepository.Holding(batch), distributedLock).HandleAsync(BatchOf(batch));

        Assert.Single(distributedLock.Acquired);
    }

    /// <summary>
    /// The cap is applied in request order, exactly as it was when the seats were
    /// held one call at a time: with two holds elsewhere, the first two seats fit
    /// and the last two do not.
    /// </summary>
    [Fact]
    public async Task HandleBatch_WhenTheCapRunsOutPartWay_ShouldRefuseTheSeatsNamedLast()
    {
        var batch = new[]
        {
            AnotherAvailableSeat(), AnotherAvailableSeat(), AnotherAvailableSeat(), AnotherAvailableSeat()
        };
        var seats = FakeSeatRepository.Holding(batch).WithLiveHolds(2);

        var results = await HandlerFor(seats).HandleAsync(BatchOf(batch));

        Assert.Equal(
            [HoldSeatOutcome.Held, HoldSeatOutcome.Held, HoldSeatOutcome.HoldCapReached, HoldSeatOutcome.HoldCapReached],
            results.Select(result => result.Outcome));
    }

    /// <summary>
    /// Why the port returns the live holds rather than a count. At the cap, a seat
    /// the client already holds is still theirs to re-hold, while a new seat is
    /// not — and a count that left the requested seats out could not tell the two
    /// apart, so it would either refuse the re-hold or let the new seat past.
    /// </summary>
    [Fact]
    public async Task HandleBatch_AtTheCap_ShouldRefuseTheNewSeatAndKeepTheOneTheyHold()
    {
        var fresh = AnotherAvailableSeat();
        var theirs = HeldBy(AnotherAvailableSeat(), ClientA);
        var seats = FakeSeatRepository.Holding(fresh, theirs)
            .WithLiveHolds(HoldSeatCommandHandler.MaxHoldsPerClientPerEvent - 1)
            .WithLiveHoldOn(theirs.Id);

        var results = await HandlerFor(seats).HandleAsync(BatchOf(fresh, theirs));

        Assert.Equal(
            [HoldSeatOutcome.HoldCapReached, HoldSeatOutcome.Held],
            results.Select(result => result.Outcome));
        Assert.Equal(SeatStatus.Available, fresh.Status);
    }

    [Fact]
    public async Task HandleBatch_WhenASeatIsMissing_ShouldAnswerItWhereItWasAsked()
    {
        var present = AnotherAvailableSeat();
        var missing = Guid.NewGuid();
        var seats = FakeSeatRepository.Holding(present);

        var results = await HandlerFor(seats)
            .HandleAsync(new HoldSeatsCommand(EventId, [missing, present.Id], ClientA));

        Assert.Equal(
            [HoldSeatOutcome.SeatNotFound, HoldSeatOutcome.Held],
            results.Select(result => result.Outcome));
    }

    [Fact]
    public async Task HandleBatch_WhenClientLockHeldByAnother_ShouldRefuseEverySeat()
    {
        var batch = new[] { AnotherAvailableSeat(), AnotherAvailableSeat() };

        var results = await HandlerFor(
                FakeSeatRepository.Holding(batch),
                new FakeDistributedLock(LockOutcome.HeldByAnother))
            .HandleAsync(BatchOf(batch));

        Assert.All(results, result => Assert.Equal(HoldSeatOutcome.ConcurrentRequestInFlight, result.Outcome));
    }

    /// <summary>
    /// A lost race rejects the whole write, so every seat the attempt moved is
    /// asked again — and the retry is what finds out which one was taken.
    /// </summary>
    [Fact]
    public async Task HandleBatch_WhenTheWriteLosesARace_ShouldRetryTheWholeBatch()
    {
        var mine = AnotherAvailableSeat();
        var contested = AnotherAvailableSeat();
        var retried = new[] { Seat.Create(mine.Id, EventId), HeldBy(Seat.Create(contested.Id, EventId), ClientB) };

        var seats = FakeSeatRepository.Loading([mine, contested], retried)
            .WithSaveOutcomes(new ConcurrentSeatModificationException(contested.Id), null);

        var results = await HandlerFor(seats).HandleAsync(BatchOf(mine, contested));

        Assert.Equal(
            [HoldSeatOutcome.Held, HoldSeatOutcome.AlreadyHeld],
            results.Select(result => result.Outcome));
        Assert.Equal(2, seats.SaveCalls);
    }

    [Fact]
    public async Task HandleBatch_WithASeatNamedTwice_ShouldThrow()
    {
        var seat = AnotherAvailableSeat();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            HandlerFor(FakeSeatRepository.Holding(seat))
                .HandleAsync(new HoldSeatsCommand(EventId, [seat.Id, seat.Id], ClientA)));
    }

    [Fact]
    public async Task HandleBatch_WithNoSeats_ShouldThrow()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            HandlerFor(FakeSeatRepository.Holding())
                .HandleAsync(new HoldSeatsCommand(EventId, [], ClientA)));
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
        private readonly List<Guid> _liveHolds = [];

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

        /// <summary>This client is holding this many seats at the event that nobody asked for.</summary>
        public FakeSeatRepository WithLiveHolds(int count)
        {
            _liveHolds.AddRange(Enumerable.Range(0, count).Select(_ => Guid.NewGuid()));
            return this;
        }

        /// <summary>This client is holding this particular seat live.</summary>
        public FakeSeatRepository WithLiveHoldOn(Guid seatId)
        {
            _liveHolds.Add(seatId);
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

        public Task<IReadOnlyCollection<Guid>> FindLiveHoldsAsync(
            Guid clientId,
            Guid eventId,
            DateTime utcNow,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Guid>>(_liveHolds);

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
            throw new InvalidOperationException("Holding does not create seats.");

        private static IReadOnlyList<IReadOnlyList<Seat>> ScriptOfSingles(Seat?[] loads)
        {
            var script = loads.Length == 0 ? new Seat?[] { null } : loads;

            return [.. script.Select(seat => seat is null ? Array.Empty<Seat>() : new[] { seat })];
        }
    }

    /// <summary>
    /// A lock whose answer is set once for every resource. There is only one lock
    /// on this path now, so a per-resource answer has nothing left to tell apart.
    /// </summary>
    private sealed class FakeDistributedLock(LockOutcome outcome = LockOutcome.Acquired) : IDistributedLock
    {
        /// <summary>Resources locked, in acquisition order.</summary>
        public List<string> Acquired { get; } = [];

        /// <summary>Resources unlocked, in release order.</summary>
        public List<string> Released { get; } = [];

        public int ReleaseCalls => Released.Count;

        public TimeSpan LastTtl { get; private set; }

        public Task<LockAcquisition> TryAcquireAsync(
            string resource,
            TimeSpan ttl,
            CancellationToken cancellationToken = default)
        {
            LastTtl = ttl;

            if (outcome is not LockOutcome.Acquired)
            {
                return Task.FromResult(outcome is LockOutcome.HeldByAnother
                    ? LockAcquisition.HeldByAnother
                    : LockAcquisition.Unavailable);
            }

            Acquired.Add(resource);
            return Task.FromResult(LockAcquisition.Acquired("token"));
        }

        public Task<bool> ReleaseAsync(
            string resource,
            string token,
            CancellationToken cancellationToken = default)
        {
            Released.Add(resource);
            return Task.FromResult(true);
        }
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
