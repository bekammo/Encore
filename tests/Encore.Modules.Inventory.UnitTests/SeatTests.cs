using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Events;
using Encore.Modules.Inventory.Domain.Exceptions;

namespace Encore.Modules.Inventory.UnitTests;

/// <summary>
/// The seat state machine: legal transitions, refused transitions, and lazy
/// expiry. Time is passed in, never read, so "the hold lapsed a minute ago" is a
/// parameter rather than a sleep — every test here runs in microseconds and
/// touches no infrastructure.
/// </summary>
public class SeatTests
{
    private static readonly Guid SeatId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EventId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ClientA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ClientB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    /// <summary>Arbitrary fixed instant. Everything else is expressed relative to it.</summary>
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime WithinHold = T0.AddMinutes(4);
    private static readonly DateTime AfterHold = T0.AddMinutes(6);

    private static Seat Available() => Seat.Create(SeatId, EventId);

    private static Seat HeldBy(Guid clientId, DateTime at)
    {
        var seat = Available();
        seat.Hold(clientId, at);
        seat.ClearDomainEvents();
        return seat;
    }

    private static Seat SoldTo(Guid clientId)
    {
        var seat = HeldBy(clientId, T0);
        seat.Sell(clientId, WithinHold);
        seat.ClearDomainEvents();
        return seat;
    }

    // -- Hold -------------------------------------------------------------

    [Fact]
    public void Hold_WhenSeatAvailable_ShouldSucceed()
    {
        var seat = Available();

        seat.Hold(ClientA, T0);

        Assert.Equal(SeatStatus.Held, seat.Status);
        Assert.Equal(ClientA, seat.HeldByClientId);
    }

    [Fact]
    public void Hold_ShouldSetExpiryToFiveMinutesFromNow()
    {
        var seat = Available();

        seat.Hold(ClientA, T0);

        Assert.Equal(T0.AddMinutes(5), seat.HoldExpiresAt);
    }

    [Fact]
    public void Hold_WhenSuccessful_ShouldRaiseSeatHeld()
    {
        var seat = Available();

        seat.Hold(ClientA, T0);

        var held = Assert.IsType<SeatHeld>(Assert.Single(seat.DomainEvents));
        Assert.Equal(SeatId, held.SeatId);
        Assert.Equal(EventId, held.EventId);
        Assert.Equal(ClientA, held.ClientId);
        Assert.Equal(T0.AddMinutes(5), held.HoldExpiresAt);
        Assert.Equal(T0, held.OccurredAt);
    }

    [Fact]
    public void Hold_WhenSeatAlreadyHeldByAnotherClient_ShouldThrow()
    {
        var seat = HeldBy(ClientA, T0);

        var ex = Assert.Throws<SeatTransitionException>(() => seat.Hold(ClientB, WithinHold));
        Assert.Equal(SeatTransitionReason.SeatAlreadyHeld, ex.Reason);
    }

    [Fact]
    public void Hold_WhenSeatSold_ShouldThrow()
    {
        var seat = SoldTo(ClientA);

        var ex = Assert.Throws<SeatTransitionException>(() => seat.Hold(ClientB, WithinHold));
        Assert.Equal(SeatTransitionReason.SeatAlreadySold, ex.Reason);
    }

    [Fact]
    public void Hold_WhenRefused_ShouldRaiseNoEvents()
    {
        var seat = HeldBy(ClientA, T0);

        Assert.Throws<SeatTransitionException>(() => seat.Hold(ClientB, WithinHold));

        Assert.Empty(seat.DomainEvents);
    }

    /// <summary>
    /// The lazy half of expiry. This must pass with no sweep having run anywhere
    /// near this seat — if it ever needs one, the sweep has become load-bearing.
    /// </summary>
    [Fact]
    public void Hold_WhenExistingHoldHasAlreadyExpired_ShouldSucceed()
    {
        var seat = HeldBy(ClientA, T0);

        seat.Hold(ClientB, AfterHold);

        Assert.Equal(SeatStatus.Held, seat.Status);
        Assert.Equal(ClientB, seat.HeldByClientId);
        Assert.Equal(AfterHold.AddMinutes(5), seat.HoldExpiresAt);
    }

    [Fact]
    public void Hold_WhenReclaimingExpiredHold_ShouldRaiseReleasedThenHeld()
    {
        var seat = HeldBy(ClientA, T0);

        seat.Hold(ClientB, AfterHold);

        Assert.Collection(
            seat.DomainEvents,
            e =>
            {
                var released = Assert.IsType<SeatReleased>(e);
                Assert.Equal(ClientA, released.ClientId);
                Assert.Equal(SeatReleaseReason.Expired, released.Reason);
            },
            e =>
            {
                var held = Assert.IsType<SeatHeld>(e);
                Assert.Equal(ClientB, held.ClientId);
            });
    }

    [Fact]
    public void Hold_WhenSameClientHoldsAgain_ShouldLeaveExpiryUnchanged()
    {
        var seat = HeldBy(ClientA, T0);

        seat.Hold(ClientA, WithinHold);

        Assert.Equal(SeatStatus.Held, seat.Status);
        Assert.Equal(ClientA, seat.HeldByClientId);
        Assert.Equal(T0.AddMinutes(5), seat.HoldExpiresAt);
    }

    [Fact]
    public void Hold_WhenSameClientHoldsAgain_ShouldRaiseNoSecondEvent()
    {
        var seat = HeldBy(ClientA, T0);

        seat.Hold(ClientA, WithinHold);

        Assert.Empty(seat.DomainEvents);
    }

    [Fact]
    public void Hold_WhenSameClientHoldsAfterOwnHoldExpired_ShouldReclaim()
    {
        var seat = HeldBy(ClientA, T0);

        seat.Hold(ClientA, AfterHold);

        Assert.Equal(AfterHold.AddMinutes(5), seat.HoldExpiresAt);
        Assert.Equal(2, seat.DomainEvents.Count);
    }

    // -- Release ----------------------------------------------------------

    [Fact]
    public void Release_WhenSeatHeld_ShouldReturnSeatToAvailable()
    {
        var seat = HeldBy(ClientA, T0);

        seat.Release(ClientA, WithinHold);

        Assert.Equal(SeatStatus.Available, seat.Status);
    }

    [Fact]
    public void Release_WhenSuccessful_ShouldClearHoldingClientAndExpiry()
    {
        var seat = HeldBy(ClientA, T0);

        seat.Release(ClientA, WithinHold);

        Assert.Null(seat.HeldByClientId);
        Assert.Null(seat.HoldExpiresAt);
    }

    [Fact]
    public void Release_WhenSeatHeld_ShouldRaiseSeatReleasedAsCancelled()
    {
        var seat = HeldBy(ClientA, T0);

        seat.Release(ClientA, WithinHold);

        var released = Assert.IsType<SeatReleased>(Assert.Single(seat.DomainEvents));
        Assert.Equal(ClientA, released.ClientId);
        Assert.Equal(SeatReleaseReason.Cancelled, released.Reason);
        Assert.Equal(WithinHold, released.OccurredAt);
    }

    [Fact]
    public void Release_WhenSeatAlreadyAvailable_ShouldBeNoOp()
    {
        var seat = Available();

        seat.Release(ClientA, T0);

        Assert.Equal(SeatStatus.Available, seat.Status);
        Assert.Empty(seat.DomainEvents);
    }

    [Fact]
    public void Release_WhenOwnHoldAlreadyExpired_ShouldBeNoOp()
    {
        var seat = HeldBy(ClientA, T0);

        seat.Release(ClientA, AfterHold);

        Assert.Empty(seat.DomainEvents);
    }

    [Fact]
    public void Release_WhenRequestedByDifferentClientThanHolder_ShouldThrow()
    {
        var seat = HeldBy(ClientA, T0);

        var ex = Assert.Throws<SeatTransitionException>(() => seat.Release(ClientB, WithinHold));
        Assert.Equal(SeatTransitionReason.NotTheHolder, ex.Reason);
    }

    [Fact]
    public void Release_WhenSeatSold_ShouldThrow()
    {
        var seat = SoldTo(ClientA);

        var ex = Assert.Throws<SeatTransitionException>(() => seat.Release(ClientA, WithinHold));
        Assert.Equal(SeatTransitionReason.SeatAlreadySold, ex.Reason);
    }

    // -- Sell -------------------------------------------------------------

    [Fact]
    public void Sell_WhenHoldIsLive_ShouldSucceed()
    {
        var seat = HeldBy(ClientA, T0);

        seat.Sell(ClientA, WithinHold);

        Assert.Equal(SeatStatus.Sold, seat.Status);
    }

    [Fact]
    public void Sell_WhenSuccessful_ShouldRecordBuyerAndClearExpiry()
    {
        var seat = HeldBy(ClientA, T0);

        seat.Sell(ClientA, WithinHold);

        Assert.Equal(ClientA, seat.HeldByClientId);
        Assert.Null(seat.HoldExpiresAt);
    }

    [Fact]
    public void Sell_WhenSuccessful_ShouldRaiseSeatSold()
    {
        var seat = HeldBy(ClientA, T0);

        seat.Sell(ClientA, WithinHold);

        var sold = Assert.IsType<SeatSold>(Assert.Single(seat.DomainEvents));
        Assert.Equal(SeatId, sold.SeatId);
        Assert.Equal(EventId, sold.EventId);
        Assert.Equal(ClientA, sold.ClientId);
        Assert.Equal(WithinHold, sold.OccurredAt);
    }

    [Fact]
    public void Sell_WhenHoldExpired_ShouldThrow()
    {
        var seat = HeldBy(ClientA, T0);

        var ex = Assert.Throws<SeatTransitionException>(() => seat.Sell(ClientA, AfterHold));
        Assert.Equal(SeatTransitionReason.HoldExpired, ex.Reason);
    }

    /// <summary>
    /// The refusal reason turns on who is asking, not on what the row says. A
    /// client who never held the seat is told so, even though the lapsed hold
    /// still sitting on the row makes their request look, to the row alone,
    /// exactly like the holder's own expired one.
    /// </summary>
    [Fact]
    public void Sell_WhenAnotherClientsHoldHasLapsed_ShouldSayNotTheHolder()
    {
        var seat = HeldBy(ClientA, T0);

        var ex = Assert.Throws<SeatTransitionException>(() => seat.Sell(ClientB, AfterHold));
        Assert.Equal(SeatTransitionReason.NotTheHolder, ex.Reason);
    }

    [Fact]
    public void Sell_WhenSeatAvailableWithNoHold_ShouldThrow()
    {
        var seat = Available();

        var ex = Assert.Throws<SeatTransitionException>(() => seat.Sell(ClientA, T0));
        Assert.Equal(SeatTransitionReason.NoActiveHold, ex.Reason);
    }

    [Fact]
    public void Sell_WhenHoldBelongsToDifferentClient_ShouldThrow()
    {
        var seat = HeldBy(ClientA, T0);

        var ex = Assert.Throws<SeatTransitionException>(() => seat.Sell(ClientB, WithinHold));
        Assert.Equal(SeatTransitionReason.NotTheHolder, ex.Reason);
    }

    [Fact]
    public void Sell_WhenSeatAlreadySold_ShouldThrow()
    {
        var seat = SoldTo(ClientA);

        var ex = Assert.Throws<SeatTransitionException>(() => seat.Sell(ClientA, WithinHold));
        Assert.Equal(SeatTransitionReason.SeatAlreadySold, ex.Reason);
    }

    [Fact]
    public void Sell_WhenRefused_ShouldRaiseNoEvents()
    {
        var seat = HeldBy(ClientA, T0);

        Assert.Throws<SeatTransitionException>(() => seat.Sell(ClientB, WithinHold));

        Assert.Empty(seat.DomainEvents);
    }
}
