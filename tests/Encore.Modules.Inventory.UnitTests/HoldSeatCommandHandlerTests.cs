using Encore.Modules.Inventory.Application;
using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.UnitTests;

/// <summary>
/// The hold use case, driven entirely through fake ports. No database, no Redis,
/// no container — which is the whole return on the hexagon: the sequencing logic
/// is testable in microseconds, and the two decisions that matter most here (the
/// lock is optional, a lost race is retried once) are asserted directly rather
/// than inferred from a load test.
/// </summary>
public class HoldSeatCommandHandlerTests
{
    private static readonly Guid SeatId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EventId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ClientA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ClientB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static HoldSeatCommand Command => new(SeatId, ClientA);

    private static Seat AvailableSeat() => Seat.Create(SeatId, EventId);

    private static Seat SeatHeldBy(Guid clientId)
    {
        var seat = AvailableSeat();
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
        var distributedLock = new FakeDistributedLock(acquires: false);

        var result = await HandlerFor(seats, distributedLock).HandleAsync(Command);

        Assert.Equal(HoldSeatOutcome.Held, result.Outcome);
        Assert.Equal(1, seats.SaveCalls);
    }

    [Fact]
    public async Task Handle_WhenLockCannotBeAcquired_ShouldNotReleaseSomebodyElsesLock()
    {
        var seats = new FakeSeatRepository(AvailableSeat());
        var distributedLock = new FakeDistributedLock(acquires: false);

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
    public async Task Handle_WhenAttemptRefused_ShouldStillReleaseLock()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientB));
        var distributedLock = new FakeDistributedLock();

        await HandlerFor(seats, distributedLock).HandleAsync(Command);

        Assert.Equal(1, distributedLock.ReleaseCalls);
    }

    [Fact]
    public async Task Handle_ShouldLockOnTheSeatBeingHeld()
    {
        var seats = new FakeSeatRepository(AvailableSeat());
        var distributedLock = new FakeDistributedLock();

        await HandlerFor(seats, distributedLock).HandleAsync(Command);

        Assert.Equal($"seat:{SeatId}", distributedLock.LastResource);
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

    // -- Fakes ------------------------------------------------------------

    private sealed class FakeSeatRepository(params Seat?[] loads) : ISeatRepository
    {
        private readonly Seat?[] _loads = loads.Length == 0 ? [null] : loads;
        private readonly List<Exception?> _saveOutcomes = [];

        public int GetByIdCalls { get; private set; }

        public int SaveCalls { get; private set; }

        /// <summary>One entry per expected save: an exception to throw, or null to succeed.</summary>
        public FakeSeatRepository WithSaveOutcomes(params Exception?[] outcomes)
        {
            _saveOutcomes.AddRange(outcomes);
            return this;
        }

        public Task<Seat?> GetByIdAsync(Guid seatId, CancellationToken cancellationToken = default)
        {
            var seat = _loads[Math.Min(GetByIdCalls, _loads.Length - 1)];
            GetByIdCalls++;
            return Task.FromResult(seat);
        }

        public Task SaveAsync(Seat seat, CancellationToken cancellationToken = default)
        {
            var outcome = SaveCalls < _saveOutcomes.Count ? _saveOutcomes[SaveCalls] : null;
            SaveCalls++;

            return outcome is null ? Task.CompletedTask : Task.FromException(outcome);
        }
    }

    private sealed class FakeDistributedLock(bool acquires = true) : IDistributedLock
    {
        public int ReleaseCalls { get; private set; }

        public string? LastResource { get; private set; }

        public TimeSpan LastTtl { get; private set; }

        public Task<string?> TryAcquireAsync(
            string resource,
            TimeSpan ttl,
            CancellationToken cancellationToken = default)
        {
            LastResource = resource;
            LastTtl = ttl;

            return Task.FromResult(acquires ? "token" : null);
        }

        public Task<bool> ReleaseAsync(
            string resource,
            string token,
            CancellationToken cancellationToken = default)
        {
            ReleaseCalls++;
            return Task.FromResult(true);
        }
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
