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

    private static SellSeatCommand Command => new(SeatId, ClientA);

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

    private static SellSeatCommandHandler HandlerFor(
        FakeSeatRepository seats,
        FakeDistributedLock? distributedLock = null,
        DateTime? now = null) =>
        new(seats, distributedLock ?? new FakeDistributedLock(), new FixedTimeProvider(now ?? WithinHold));

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

    // -- The lock stays an optimisation on this path too -------------------

    /// <summary>
    /// The sale is the most expensive thing to get wrong, and it still must not
    /// depend on Redis. Correctness is the concurrency token's job here as well.
    /// </summary>
    [Fact]
    public async Task Handle_WhenLockCannotBeAcquired_ShouldStillCompleteTheSale()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientA));
        var distributedLock = new FakeDistributedLock(LockOutcome.Unavailable);

        var result = await HandlerFor(seats, distributedLock).HandleAsync(Command);

        Assert.Equal(SellSeatOutcome.Sold, result.Outcome);
        Assert.Equal(1, seats.SaveCalls);
    }

    [Fact]
    public async Task Handle_WhenLockCannotBeAcquired_ShouldNotReleaseSomebodyElsesLock()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientA));
        var distributedLock = new FakeDistributedLock(LockOutcome.Unavailable);

        await HandlerFor(seats, distributedLock).HandleAsync(Command);

        Assert.Equal(0, distributedLock.ReleaseCalls);
    }

    [Fact]
    public async Task Handle_WhenLockAcquired_ShouldReleaseIt()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientA));
        var distributedLock = new FakeDistributedLock();

        await HandlerFor(seats, distributedLock).HandleAsync(Command);

        Assert.Equal(1, distributedLock.ReleaseCalls);
    }

    [Fact]
    public async Task Handle_ShouldLockOnTheSeatBeingSold()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientA));
        var distributedLock = new FakeDistributedLock();

        await HandlerFor(seats, distributedLock).HandleAsync(Command);

        Assert.Equal($"seat:{SeatId}", distributedLock.LastResource);
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
        /// Never called on this path: the hold cap counts holds, and selling one
        /// releases capacity rather than consuming it. Throwing rather than
        /// returning zero keeps that a fact the tests would catch changing.
        /// </summary>
        public Task<int> CountLiveHoldsAsync(
            Guid clientId,
            Guid eventId,
            Guid excludingSeatId,
            DateTime utcNow,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Selling does not consult the hold cap.");
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
