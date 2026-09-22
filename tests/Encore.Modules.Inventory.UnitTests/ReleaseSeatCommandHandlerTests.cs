using Encore.Modules.Inventory.Application;
using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.UnitTests;

/// <summary>
/// The give-it-back use case. The theme worth reading for is that almost
/// everything here is a success: a client asking not to hold a seat gets what
/// they asked for whether or not they were holding it, because the state they
/// want already holds.
/// </summary>
public class ReleaseSeatCommandHandlerTests
{
    private static readonly Guid SeatId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EventId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ClientA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ClientB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Inside the 5-minute hold taken at <see cref="T0"/>.</summary>
    private static readonly DateTime WithinHold = T0.AddMinutes(4);

    /// <summary>After it has lapsed.</summary>
    private static readonly DateTime AfterHold = T0.AddMinutes(6);

    private static ReleaseSeatCommand Command => new(EventId, SeatId, ClientA);

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

    private static ReleaseSeatCommandHandler HandlerFor(
        FakeSeatRepository seats,
        FakeDistributedLock? distributedLock = null,
        DateTime? now = null) =>
        new(seats, distributedLock ?? new FakeDistributedLock(), new FixedTimeProvider(now ?? WithinHold));

    // -- Happy path -------------------------------------------------------

    [Fact]
    public async Task Handle_WhenClientHoldsTheSeat_ShouldReturnReleased()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientA));

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(ReleaseSeatOutcome.Released, result.Outcome);
    }

    [Fact]
    public async Task Handle_WhenClientHoldsTheSeat_ShouldReturnItToThePool()
    {
        var seat = SeatHeldBy(ClientA);
        var seats = new FakeSeatRepository(seat);

        await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(1, seats.SaveCalls);
        Assert.Equal(SeatStatus.Available, seat.Status);
        Assert.Null(seat.HeldByClientId);
        Assert.Null(seat.HoldExpiresAt);
    }

    [Fact]
    public async Task Handle_WhenReleased_ShouldRaiseSeatReleasedAsCancelled()
    {
        var seat = SeatHeldBy(ClientA);
        var seats = new FakeSeatRepository(seat);

        await HandlerFor(seats).HandleAsync(Command);

        var released = Assert.IsType<Domain.Events.SeatReleased>(Assert.Single(seat.DomainEvents));
        Assert.Equal(Domain.Events.SeatReleaseReason.Cancelled, released.Reason);
        Assert.Equal(ClientA, released.ClientId);
    }

    // -- Asking for a state that already holds is a success ---------------

    [Fact]
    public async Task Handle_WhenSeatIsAlreadyAvailable_ShouldReturnReleased()
    {
        var seats = new FakeSeatRepository(AvailableSeat());

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(ReleaseSeatOutcome.Released, result.Outcome);
    }

    [Fact]
    public async Task Handle_WhenSeatIsAlreadyAvailable_ShouldRaiseNoEvent()
    {
        var seat = AvailableSeat();
        var seats = new FakeSeatRepository(seat);

        await HandlerFor(seats).HandleAsync(Command);

        Assert.Empty(seat.DomainEvents);
    }

    /// <summary>
    /// The hold lapsed while the request was in flight. The client wanted not to
    /// be holding the seat; they are not. Refusing would be pedantry.
    /// </summary>
    [Fact]
    public async Task Handle_WhenOwnHoldHasAlreadyLapsed_ShouldReturnReleased()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientA));

        var result = await HandlerFor(seats, now: AfterHold).HandleAsync(Command);

        Assert.Equal(ReleaseSeatOutcome.Released, result.Outcome);
    }

    // -- Refusals ---------------------------------------------------------

    [Fact]
    public async Task Handle_WhenSomebodyElseHoldsTheSeat_ShouldReturnNotTheHolder()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientB));

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(ReleaseSeatOutcome.NotTheHolder, result.Outcome);
        Assert.Equal(0, seats.SaveCalls);
    }

    /// <summary>A sale is not undone by asking to release the seat.</summary>
    [Fact]
    public async Task Handle_WhenSeatIsSold_ShouldReturnAlreadySold()
    {
        var seats = new FakeSeatRepository(SeatSoldTo(ClientA));

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(ReleaseSeatOutcome.AlreadySold, result.Outcome);
        Assert.Equal(0, seats.SaveCalls);
    }

    [Fact]
    public async Task Handle_WhenSeatDoesNotExist_ShouldReturnSeatNotFound()
    {
        var seats = new FakeSeatRepository([null]);

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(ReleaseSeatOutcome.SeatNotFound, result.Outcome);
    }

    [Fact]
    public async Task Handle_WhenSeatBelongsToADifferentEvent_ShouldReportNotFound()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientA));
        var wrongEvent = new ReleaseSeatCommand(Guid.NewGuid(), SeatId, ClientA);

        var result = await HandlerFor(seats).HandleAsync(wrongEvent);

        Assert.Equal(ReleaseSeatOutcome.SeatNotFound, result.Outcome);
        Assert.Equal(0, seats.SaveCalls);
    }

    // -- Locking behaves as it does on the other write paths ---------------

    [Fact]
    public async Task Handle_WhenLockServiceUnavailable_ShouldStillRelease()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientA));

        var result = await HandlerFor(seats, new FakeDistributedLock(LockOutcome.Unavailable))
            .HandleAsync(Command);

        Assert.Equal(ReleaseSeatOutcome.Released, result.Outcome);
        Assert.Equal(1, seats.SaveCalls);
    }

    /// <summary>
    /// Unlike holding, contention here is not refused: there is no cap for a
    /// missed lock to undermine, and the row's concurrency token settles the
    /// race on its own.
    /// </summary>
    [Fact]
    public async Task Handle_WhenSeatLockHeldByAnother_ShouldStillRelease()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientA));

        var result = await HandlerFor(seats, new FakeDistributedLock(LockOutcome.HeldByAnother))
            .HandleAsync(Command);

        Assert.Equal(ReleaseSeatOutcome.Released, result.Outcome);
    }

    [Fact]
    public async Task Handle_ShouldLockOnTheSeat()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientA));
        var distributedLock = new FakeDistributedLock();

        await HandlerFor(seats, distributedLock).HandleAsync(Command);

        Assert.Equal($"seat:{SeatId}", distributedLock.LastResource);
        Assert.Equal(1, distributedLock.ReleaseCalls);
    }

    // -- A lost race is retried exactly once ------------------------------

    [Fact]
    public async Task Handle_WhenFirstWriteLosesTheRace_ShouldReloadAndSucceed()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientA), SeatHeldBy(ClientA))
            .WithSaveOutcomes(new ConcurrentSeatModificationException(SeatId), null);

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(ReleaseSeatOutcome.Released, result.Outcome);
        Assert.Equal(2, seats.GetByIdCalls);
    }

    [Fact]
    public async Task Handle_WhenBothAttemptsLoseTheRace_ShouldReportLostRace()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientA), SeatHeldBy(ClientA))
            .WithSaveOutcomes(
                new ConcurrentSeatModificationException(SeatId),
                new ConcurrentSeatModificationException(SeatId));

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(ReleaseSeatOutcome.LostRace, result.Outcome);
        Assert.Equal(2, seats.SaveCalls);
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

        /// <summary>
        /// Never called here: releasing gives hold capacity back rather than
        /// consuming it, so the cap has nothing to say. Throwing keeps that a
        /// fact the tests would notice changing.
        /// </summary>
        public Task<int> CountLiveHoldsAsync(
            Guid clientId,
            Guid eventId,
            Guid excludingSeatId,
            DateTime utcNow,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Releasing does not consult the hold cap.");

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
    }

    private sealed class FakeDistributedLock(LockOutcome outcome = LockOutcome.Acquired) : IDistributedLock
    {
        public int ReleaseCalls { get; private set; }

        public string? LastResource { get; private set; }

        public Task<LockAcquisition> TryAcquireAsync(
            string resource,
            TimeSpan ttl,
            CancellationToken cancellationToken = default)
        {
            LastResource = resource;

            return Task.FromResult(outcome switch
            {
                LockOutcome.Acquired => LockAcquisition.Acquired("token"),
                LockOutcome.HeldByAnother => LockAcquisition.HeldByAnother,
                _ => LockAcquisition.Unavailable
            });
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
