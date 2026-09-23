using Encore.Modules.Inventory.Application;
using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.UnitTests;

/// <summary>
/// The release use case. Almost everything is a success: a client who asks not to hold a seat
/// gets that state whether or not they held it.
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

    private static Seat AnotherSeatHeldBy(Guid clientId)
    {
        var seat = Seat.Create(Guid.NewGuid(), EventId);
        seat.Hold(clientId, T0);
        seat.ClearDomainEvents();
        return seat;
    }

    private static ReleaseSeatsCommand BatchOf(params Seat[] seats) =>
        new(EventId, [.. seats.Select(seat => seat.Id)], ClientA);

    private static ReleaseSeatCommandHandler HandlerFor(FakeSeatRepository seats, DateTime? now = null) =>
        new(seats, new FixedTimeProvider(now ?? WithinHold));

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

    /// <summary>The hold lapsed while the request was in flight: still a success.</summary>
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
    public async Task Handle_WhenSeatIsSoldToSomebodyElse_ShouldReturnAlreadySold()
    {
        var seats = new FakeSeatRepository(SeatSoldTo(ClientB));

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(ReleaseSeatOutcome.AlreadySold, result.Outcome);
        Assert.Equal(0, seats.SaveCalls);
    }

    /// <summary>Sold to this client: a cancel racing its own confirm reads this and backs off.</summary>
    [Fact]
    public async Task Handle_WhenSeatIsSoldToThisClient_ShouldReturnSoldToYou()
    {
        var seats = new FakeSeatRepository(SeatSoldTo(ClientA));

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(ReleaseSeatOutcome.SoldToYou, result.Outcome);
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

    // -- Batches ------------------------------------------------------------

    /// <summary>Every seat goes back in one write; one that cannot does not keep the others held.</summary>
    [Fact]
    public async Task HandleBatch_WhenOneSeatIsNotTheirs_ShouldStillReleaseTheOthersInOneSave()
    {
        var first = AnotherSeatHeldBy(ClientA);
        var theirs = AnotherSeatHeldBy(ClientB);
        var last = AnotherSeatHeldBy(ClientA);
        var seats = FakeSeatRepository.Holding(first, theirs, last);

        var results = await HandlerFor(seats).HandleAsync(BatchOf(first, theirs, last));

        Assert.Equal(
            [ReleaseSeatOutcome.Released, ReleaseSeatOutcome.NotTheHolder, ReleaseSeatOutcome.Released],
            results.Select(result => result.Outcome));
        Assert.Equal(1, seats.SaveCalls);
        Assert.Equal(SeatStatus.Available, first.Status);
        Assert.Equal(SeatStatus.Available, last.Status);
    }

    [Fact]
    public async Task HandleBatch_WhenNothingIsHeld_ShouldSucceedWithoutWriting()
    {
        var batch = new[] { Seat.Create(Guid.NewGuid(), EventId), Seat.Create(Guid.NewGuid(), EventId) };
        var seats = FakeSeatRepository.Holding(batch);

        var results = await HandlerFor(seats).HandleAsync(BatchOf(batch));

        Assert.All(results, result => Assert.Equal(ReleaseSeatOutcome.Released, result.Outcome));
        Assert.Equal(0, seats.SaveCalls);
    }

    // -- Fakes ------------------------------------------------------------

    /// <summary>Answers each load from a script: one entry per call, the last repeated.</summary>
    private sealed class FakeSeatRepository : ISeatRepository
    {
        private readonly IReadOnlyList<IReadOnlyList<Seat>> _loads;
        private readonly List<Exception?> _saveOutcomes = [];

        public FakeSeatRepository(params Seat?[] loads) => _loads = ScriptOfSingles(loads);

        private FakeSeatRepository(IReadOnlyList<IReadOnlyList<Seat>> loads, bool scripted) => _loads = loads;

        public int GetByIdCalls { get; private set; }

        public int SaveCalls { get; private set; }

        /// <summary>Every call returns these seats.</summary>
        public static FakeSeatRepository Holding(params Seat[] seats) =>
            new(new IReadOnlyList<Seat>[] { seats }, scripted: true);

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

            return outcome is null ? Task.CompletedTask : Task.FromException(outcome);
        }

        /// <summary>Releasing never consults the hold cap, so this throws.</summary>
        public Task<IReadOnlyCollection<Guid>> FindLiveHoldsAsync(
            Guid clientId,
            Guid eventId,
            DateTime utcNow,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Releasing does not consult the hold cap.");

        /// <summary>The sweep's query; no handler calls it, so it throws.</summary>
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
