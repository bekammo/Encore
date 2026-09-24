using Encore.Modules.Inventory.Application;
using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Events;
using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;
using Microsoft.Extensions.Time.Testing;

namespace Encore.Modules.Inventory.UnitTests;

public sealed class ReleaseSeatCommandHandlerTests
{
    private static readonly Guid SeatId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EventId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ClientA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ClientB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime WithinHold = T0.AddMinutes(4);
    private static readonly DateTime AfterHold = T0.AddMinutes(6);

    private static ReleaseSeatCommand Command => new(EventId, SeatId, ClientA);

    private static Seat AvailableSeat() => Seat.Create(SeatId, EventId);

    private static Seat SeatHeldBy(Guid clientId) => HeldBy(AvailableSeat(), clientId);

    private static Seat SeatSoldTo(Guid clientId)
    {
        var seat = SeatHeldBy(clientId);
        seat.Sell(clientId, WithinHold);
        seat.ClearDomainEvents();
        return seat;
    }

    private static Seat AnotherSeatHeldBy(Guid clientId) =>
        HeldBy(Seat.Create(Guid.NewGuid(), EventId), clientId);

    private static Seat HeldBy(Seat seat, Guid clientId)
    {
        seat.Hold(clientId, T0);
        seat.ClearDomainEvents();
        return seat;
    }

    private static ReleaseSeatsCommand BatchOf(params Seat[] seats) =>
        new(EventId, [.. seats.Select(seat => seat.Id)], ClientA);

    private static ReleaseSeatCommandHandler HandlerFor(FakeSeatRepository seats, DateTime? now = null) =>
        new(seats, new FakeTimeProvider(now ?? WithinHold));

    // -- Happy path -------------------------------------------------------

    [Fact]
    public async Task Handle_WhenClientHoldsTheSeat_ShouldReturnReleased()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientA));

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(ReleaseSeatOutcome.Released, result);
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

        var released = Assert.IsType<SeatReleased>(Assert.Single(seat.DomainEvents));
        Assert.Equal(SeatReleaseReason.Cancelled, released.Reason);
        Assert.Equal(ClientA, released.ClientId);
    }

    // -- Asking for a state that already holds is a success ---------------

    [Fact]
    public async Task Handle_WhenSeatIsAlreadyAvailable_ShouldReturnReleased()
    {
        var seats = new FakeSeatRepository(AvailableSeat());

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(ReleaseSeatOutcome.Released, result);
    }

    [Fact]
    public async Task Handle_WhenSeatIsAlreadyAvailable_ShouldRaiseNoEvent()
    {
        var seat = AvailableSeat();
        var seats = new FakeSeatRepository(seat);

        await HandlerFor(seats).HandleAsync(Command);

        Assert.Empty(seat.DomainEvents);
    }

    [Fact]
    public async Task Handle_WhenOwnHoldHasAlreadyLapsed_ShouldReturnReleased()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientA));

        var result = await HandlerFor(seats, now: AfterHold).HandleAsync(Command);

        Assert.Equal(ReleaseSeatOutcome.Released, result);
    }

    // -- Refusals ---------------------------------------------------------

    [Fact]
    public async Task Handle_WhenSomebodyElseHoldsTheSeat_ShouldReturnNotTheHolder()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientB));

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(ReleaseSeatOutcome.NotTheHolder, result);
        Assert.Equal(0, seats.SaveCalls);
    }

    [Fact]
    public async Task Handle_WhenSeatIsSoldToSomebodyElse_ShouldReturnAlreadySold()
    {
        var seats = new FakeSeatRepository(SeatSoldTo(ClientB));

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(ReleaseSeatOutcome.AlreadySold, result);
        Assert.Equal(0, seats.SaveCalls);
    }

    [Fact]
    public async Task Handle_WhenSeatIsSoldToThisClient_ShouldReturnSoldToYou()
    {
        var seats = new FakeSeatRepository(SeatSoldTo(ClientA));

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(ReleaseSeatOutcome.SoldToYou, result);
        Assert.Equal(0, seats.SaveCalls);
    }

    [Fact]
    public async Task Handle_WhenSeatDoesNotExist_ShouldReturnSeatNotFound()
    {
        var seats = new FakeSeatRepository([null]);

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(ReleaseSeatOutcome.SeatNotFound, result);
    }

    [Fact]
    public async Task Handle_WhenSeatBelongsToADifferentEvent_ShouldReportNotFound()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientA));
        var wrongEvent = new ReleaseSeatCommand(Guid.NewGuid(), SeatId, ClientA);

        var result = await HandlerFor(seats).HandleAsync(wrongEvent);

        Assert.Equal(ReleaseSeatOutcome.SeatNotFound, result);
        Assert.Equal(0, seats.SaveCalls);
    }

    // -- A lost race is retried exactly once ------------------------------

    [Fact]
    public async Task Handle_WhenFirstWriteLosesTheRace_ShouldReloadAndSucceed()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientA), SeatHeldBy(ClientA))
            .WithSaveOutcomes(new ConcurrentSeatModificationException(SeatId), null);

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(ReleaseSeatOutcome.Released, result);
        Assert.Equal(2, seats.LoadCalls);
    }

    [Fact]
    public async Task Handle_WhenBothAttemptsLoseTheRace_ShouldReportLostRace()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientA), SeatHeldBy(ClientA))
            .WithSaveOutcomes(
                new ConcurrentSeatModificationException(SeatId),
                new ConcurrentSeatModificationException(SeatId));

        var result = await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(ReleaseSeatOutcome.LostRace, result);
        Assert.Equal(2, seats.SaveCalls);
    }

    /// <summary>A release that exists only in memory must not reach a later save (011).</summary>
    [Fact]
    public async Task Handle_WhenBothAttemptsLoseTheRace_ShouldReloadWhatItChanged()
    {
        var seats = new FakeSeatRepository(SeatHeldBy(ClientA), SeatHeldBy(ClientA), SeatHeldBy(ClientA))
            .WithSaveOutcomes(
                new ConcurrentSeatModificationException(SeatId),
                new ConcurrentSeatModificationException(SeatId));

        await HandlerFor(seats).HandleAsync(Command);

        Assert.Equal(3, seats.LoadCalls);
    }

    // -- Batches ----------------------------------------------------------

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
            results);
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

        Assert.All(results, result => Assert.Equal(ReleaseSeatOutcome.Released, result));
        Assert.Equal(0, seats.SaveCalls);
    }
}
