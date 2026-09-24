using Encore.Modules.Inventory.Application;
using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;
using Microsoft.Extensions.Time.Testing;

namespace Encore.Modules.Inventory.UnitTests;

/// <summary>
/// The hold use case through fake ports: no database, no Redis. The client lock's policy,
/// the single retry and batch answers are asserted directly.
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
        new(seats, distributedLock ?? new FakeDistributedLock(), new FakeTimeProvider(T0));

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
    /// With the seat lock gone and Redis unavailable, a hold still succeeds: Redis is not a
    /// correctness dependency.
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

    /// <summary>There is no seat lock; the client lock is the only one taken.</summary>
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

    /// <summary>After a lost race, the reload finds the winner's hold and reports it truthfully.</summary>
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
    /// After the second loss nothing else would reload the seats, so the handler does: a hold
    /// that exists only in memory must not reach a later save (011).
    /// </summary>
    [Fact]
    public async Task Handle_WhenBothAttemptsLoseRace_ShouldReloadWhatItChanged()
    {
        var seats = new FakeSeatRepository(AvailableSeat(), AvailableSeat(), AvailableSeat())
            .WithSaveOutcomes(
                new ConcurrentSeatModificationException(SeatId),
                new ConcurrentSeatModificationException(SeatId));

        await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(3, seats.GetByIdCalls);
    }

    /// <summary>The retry is bounded at one.</summary>
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

    /// <summary>Events raised by a rejected attempt do not survive into the retry.</summary>
    [Fact]
    public async Task Handle_WhenRetrying_ShouldDropEventsFromTheRejectedAttempt()
    {
        // The same instance comes back from the reload: the worst case for leftover events. The
        // EF adapter's batch load returns fresh instances, so the handler must not rely on that.
        var seat = AvailableSeat();
        var seats = new FakeSeatRepository(seat, seat)
            .WithSaveOutcomes(new ConcurrentSeatModificationException(SeatId), null);

        await HandlerFor(seats).HandleAsync(Command);

        // The retry finds the seat already held by this client and raises nothing; without
        // ClearDomainEvents() the rejected attempt's SeatHeld would remain.
        Assert.Empty(seat.DomainEvents);
    }

    // -- The per-client hold cap ------------------------------------------

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

    /// <summary>A client at the cap re-requesting a seat they already hold is still told yes.</summary>
    [Fact]
    public async Task Handle_WhenAtCapAndReHoldingASeatTheyAlreadyHold_ShouldSucceed()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientA))
            .WithLiveHolds(HoldSeatCommandHandler.MaxHoldsPerClientPerEvent - 1)
            .WithLiveHoldOn(SeatId);

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(HoldSeatOutcome.Held, result.Outcome);
    }

    /// <summary>With the lock unavailable the handler proceeds; the cap is best-effort.</summary>
    [Fact]
    public async Task Handle_WhenLockUnavailable_ShouldStillEnforceCapOnTheHappyPath()
    {
        var seats = new FakeSeatRepository(AvailableSeat())
            .WithLiveHolds(HoldSeatCommandHandler.MaxHoldsPerClientPerEvent);

        var result = await HandlerFor(seats, new FakeDistributedLock(LockOutcome.Unavailable))
            .HandleAsync(Command);

        // Uncontended, the count still holds the cap; the race is covered by ConcurrentHoldCapTests.
        Assert.Equal(HoldSeatOutcome.HoldCapReached, result.Outcome);
    }

    // -- The client lock has no backstop ----------------------------------

    /// <summary>
    /// A contended client lock is refused: proceeding would let concurrent requests by one
    /// client past the cap.
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

    /// <summary>An unreachable lock is not contention: the hold proceeds.</summary>
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

    // -- Batches ------------------------------------------------------------

    /// <summary>A refused seat does not cost the client the others; every seat is answered.</summary>
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

    /// <summary>The cap is applied in request order.</summary>
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
    /// At the cap, a seat the client already holds can be re-held while a new one cannot, which
    /// is why the port returns ids rather than a count.
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

    /// <summary>A lost race rejects the whole write, so every moved seat is asked again.</summary>
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

    /// <summary>A lock whose answer is the same for every resource.</summary>
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
}
