using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Events;
using Encore.Modules.Inventory.Domain.Exceptions;

namespace Encore.Modules.Inventory.UnitTests;

public sealed class SeatTests
{
    private static readonly Guid SeatId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EventId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ClientA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ClientB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime WithinHold = T0.AddMinutes(4);
    private static readonly DateTime AfterHold = T0.AddMinutes(6);

    // Expiry is exclusive: a hold is over at this instant. A literal 5, not Seat.HoldDuration,
    // so a change to the constant fails here.
    private static readonly DateTime AtExpiry = T0.AddMinutes(5);

    // A tick would not survive Postgres's microsecond precision; nothing here round-trips a database.
    private static readonly DateTime JustBeforeExpiry = AtExpiry.AddTicks(-1);

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

    // -- Create -----------------------------------------------------------

    [Fact]
    public void Create_ShouldProduceAnAvailableSeatWithNoHolderAndNoExpiry()
    {
        var seat = Seat.Create(SeatId, EventId);

        Assert.Equal(SeatId, seat.Id);
        Assert.Equal(EventId, seat.EventId);
        Assert.Equal(SeatStatus.Available, seat.Status);
        Assert.Null(seat.HeldByClientId);
        Assert.Null(seat.HoldExpiresAt);

        Assert.Empty(seat.DomainEvents);
    }

    [Fact]
    public void Create_WhenIdIsEmpty_ShouldThrow()
    {
        var ex = Assert.Throws<ArgumentException>(() => Seat.Create(Guid.Empty, EventId));

        Assert.Equal("id", ex.ParamName);
    }

    [Fact]
    public void Create_WhenEventIdIsEmpty_ShouldThrow()
    {
        var ex = Assert.Throws<ArgumentException>(() => Seat.Create(SeatId, Guid.Empty));

        Assert.Equal("eventId", ex.ParamName);
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

    /// <summary>Lazy expiry (006): must pass without any sweep having run.</summary>
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
            domainEvent =>
            {
                var released = Assert.IsType<SeatReleased>(domainEvent);
                Assert.Equal(ClientA, released.ClientId);
                Assert.Equal(SeatReleaseReason.Expired, released.Reason);
            },
            domainEvent =>
            {
                var held = Assert.IsType<SeatHeld>(domainEvent);
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

        Assert.Equal(SeatStatus.Held, seat.Status);
        Assert.Equal(ClientA, seat.HeldByClientId);
        Assert.Equal(AfterHold.AddMinutes(5), seat.HoldExpiresAt);

        Assert.Collection(
            seat.DomainEvents,
            domainEvent =>
            {
                var released = Assert.IsType<SeatReleased>(domainEvent);
                Assert.Equal(ClientA, released.ClientId);
                Assert.Equal(SeatReleaseReason.Expired, released.Reason);
                Assert.Equal(AfterHold, released.OccurredAt);
            },
            domainEvent =>
            {
                var held = Assert.IsType<SeatHeld>(domainEvent);
                Assert.Equal(ClientA, held.ClientId);
                Assert.Equal(AfterHold.AddMinutes(5), held.HoldExpiresAt);
            });
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
    public void Release_WhenOwnHoldAlreadyExpired_ShouldRaiseNoEvent()
    {
        var seat = HeldBy(ClientA, T0);

        seat.Release(ClientA, AfterHold);

        Assert.Empty(seat.DomainEvents);
    }

    [Fact]
    public void Release_WhenOwnHoldAlreadyExpired_ShouldLeaveTheStaleRowUntouched()
    {
        var seat = HeldBy(ClientA, T0);

        seat.Release(ClientA, AfterHold);

        Assert.Equal(SeatStatus.Held, seat.Status);
        Assert.Equal(ClientA, seat.HeldByClientId);
        Assert.Equal(T0.AddMinutes(5), seat.HoldExpiresAt);
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

    // -- The expiry boundary ----------------------------------------------

    [Fact]
    public void Hold_WhenExistingHoldIsOneTickFromExpiry_ShouldBeRefused()
    {
        var seat = HeldBy(ClientA, T0);

        var ex = Assert.Throws<SeatTransitionException>(() => seat.Hold(ClientB, JustBeforeExpiry));

        Assert.Equal(SeatTransitionReason.SeatAlreadyHeld, ex.Reason);
    }

    [Fact]
    public void Hold_WhenExistingHoldIsExactlyAtItsExpiry_ShouldReclaim()
    {
        var seat = HeldBy(ClientA, T0);

        seat.Hold(ClientB, AtExpiry);

        Assert.Equal(SeatStatus.Held, seat.Status);
        Assert.Equal(ClientB, seat.HeldByClientId);
        Assert.Equal(AtExpiry.AddMinutes(5), seat.HoldExpiresAt);

        Assert.Collection(
            seat.DomainEvents,
            domainEvent =>
            {
                var released = Assert.IsType<SeatReleased>(domainEvent);
                Assert.Equal(ClientA, released.ClientId);
                Assert.Equal(SeatReleaseReason.Expired, released.Reason);
            },
            domainEvent =>
            {
                var held = Assert.IsType<SeatHeld>(domainEvent);
                Assert.Equal(ClientB, held.ClientId);
            });
    }

    [Fact]
    public void Sell_WhenCalledOneTickBeforeExpiry_ShouldSucceed()
    {
        var seat = HeldBy(ClientA, T0);

        seat.Sell(ClientA, JustBeforeExpiry);

        Assert.Equal(SeatStatus.Sold, seat.Status);
    }

    [Fact]
    public void Sell_WhenCalledExactlyAtExpiry_ShouldThrowHoldExpired()
    {
        var seat = HeldBy(ClientA, T0);

        var ex = Assert.Throws<SeatTransitionException>(() => seat.Sell(ClientA, AtExpiry));

        Assert.Equal(SeatTransitionReason.HoldExpired, ex.Reason);
    }

    [Fact]
    public void Release_WhenCalledExactlyAtExpiry_ShouldBeNoOp()
    {
        var seat = HeldBy(ClientA, T0);

        seat.Release(ClientA, AtExpiry);

        Assert.Empty(seat.DomainEvents);
    }

    // -- ExpireHold -------------------------------------------------------

    [Fact]
    public void ExpireHold_WhenHoldHasLapsed_ShouldMakeTheSeatAvailable()
    {
        var seat = HeldBy(ClientA, T0);

        Assert.True(seat.ExpireHold(AfterHold));

        Assert.Equal(SeatStatus.Available, seat.Status);
    }

    [Fact]
    public void ExpireHold_WhenHoldHasLapsed_ShouldKeepTheLapsedHoldOnRecord()
    {
        var seat = HeldBy(ClientA, T0);

        seat.ExpireHold(AfterHold);

        Assert.Equal(ClientA, seat.HeldByClientId);
        Assert.Equal(AtExpiry, seat.HoldExpiresAt);
    }

    /// <summary>
    /// An order must not end Failed or Expired depending on whether the sweep had passed (006, 023).
    /// </summary>
    [Theory]
    [InlineData("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", SeatTransitionReason.HoldExpired)]
    [InlineData("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", SeatTransitionReason.NotTheHolder)]
    public void Sell_AfterTheSweep_ShouldBeRefusedAsBeforeIt(string client, SeatTransitionReason expected)
    {
        var clientId = Guid.Parse(client);

        var lazy = HeldBy(ClientA, T0);
        var swept = HeldBy(ClientA, T0);
        swept.ExpireHold(AfterHold);

        var before = Assert.Throws<SeatTransitionException>(() => lazy.Sell(clientId, AfterHold));
        var after = Assert.Throws<SeatTransitionException>(() => swept.Sell(clientId, AfterHold));

        Assert.Equal(expected, before.Reason);
        Assert.Equal(expected, after.Reason);
    }

    [Fact]
    public void Sell_AfterTheHolderReleased_ShouldSayNoActiveHold()
    {
        var seat = HeldBy(ClientA, T0);
        seat.Release(ClientA, WithinHold);

        var ex = Assert.Throws<SeatTransitionException>(() => seat.Sell(ClientA, WithinHold));

        Assert.Equal(SeatTransitionReason.NoActiveHold, ex.Reason);
    }

    /// <summary>The lapsed pair on record (023) does not make this an idempotent re-hold.</summary>
    [Fact]
    public void ExpireHold_ThenHoldByTheLapsedHolder_ShouldHoldAfreshWithOneRelease()
    {
        var seat = HeldBy(ClientA, T0);
        seat.ExpireHold(AfterHold);

        seat.Hold(ClientA, AfterHold);

        Assert.Equal(SeatStatus.Held, seat.Status);
        Assert.Equal(AfterHold.AddMinutes(5), seat.HoldExpiresAt);
        Assert.Collection(
            seat.DomainEvents,
            domainEvent => Assert.IsType<SeatReleased>(domainEvent),
            domainEvent => Assert.IsType<SeatHeld>(domainEvent));
    }

    [Fact]
    public void ExpireHold_WhenHoldHasLapsed_ShouldRaiseReleasedExpiredForTheLapsedHolder()
    {
        var seat = HeldBy(ClientA, T0);

        seat.ExpireHold(AfterHold);

        var released = Assert.IsType<SeatReleased>(Assert.Single(seat.DomainEvents));
        Assert.Equal(ClientA, released.ClientId);
        Assert.Equal(SeatReleaseReason.Expired, released.Reason);
        Assert.Equal(AfterHold, released.OccurredAt);
    }

    [Fact]
    public void ExpireHold_WhenHoldIsStillLive_ShouldChangeNothing()
    {
        var seat = HeldBy(ClientA, T0);

        Assert.False(seat.ExpireHold(WithinHold));

        Assert.Equal(SeatStatus.Held, seat.Status);
        Assert.Equal(ClientA, seat.HeldByClientId);
        Assert.Equal(T0.AddMinutes(5), seat.HoldExpiresAt);
        Assert.Empty(seat.DomainEvents);
    }

    [Fact]
    public void ExpireHold_WhenSeatIsAvailable_ShouldChangeNothing()
    {
        var seat = Available();

        Assert.False(seat.ExpireHold(AfterHold));

        Assert.Equal(SeatStatus.Available, seat.Status);
        Assert.Empty(seat.DomainEvents);
    }

    /// <summary>A sale between the sweep's query and its visit is an ordinary outcome.</summary>
    [Fact]
    public void ExpireHold_WhenSeatIsSold_ShouldChangeNothingRatherThanThrow()
    {
        var seat = SoldTo(ClientA);

        Assert.False(seat.ExpireHold(AfterHold));

        Assert.Equal(SeatStatus.Sold, seat.Status);
        Assert.Equal(ClientA, seat.HeldByClientId);
        Assert.Empty(seat.DomainEvents);
    }

    [Fact]
    public void ExpireHold_AtExactlyTheExpiryInstant_ShouldExpire()
    {
        var seat = HeldBy(ClientA, T0);

        Assert.True(seat.ExpireHold(AtExpiry));
    }

    [Fact]
    public void ExpireHold_OneTickBeforeExpiry_ShouldChangeNothing()
    {
        var seat = HeldBy(ClientA, T0);

        Assert.False(seat.ExpireHold(JustBeforeExpiry));
    }

    [Fact]
    public void ExpireHold_WhenCalledTwice_ShouldRaiseOneEvent()
    {
        var seat = HeldBy(ClientA, T0);

        Assert.True(seat.ExpireHold(AfterHold));
        Assert.False(seat.ExpireHold(AfterHold));

        Assert.Single(seat.DomainEvents);
    }

    /// <summary>Why switching the sweep off cannot change an invariant (006).</summary>
    [Fact]
    public void ExpireHold_ThenHoldByAnotherClient_ShouldLeaveTheSameLogAsALazyReclaim()
    {
        var swept = HeldBy(ClientA, T0);
        swept.ExpireHold(AfterHold);
        swept.Hold(ClientB, AfterHold);

        var lazy = HeldBy(ClientA, T0);
        lazy.Hold(ClientB, AfterHold);

        Assert.Equal(lazy.Status, swept.Status);
        Assert.Equal(lazy.HeldByClientId, swept.HeldByClientId);
        Assert.Equal(lazy.HoldExpiresAt, swept.HoldExpiresAt);

        Assert.Equal(
            lazy.DomainEvents.Select(domainEvent => domainEvent.GetType().Name),
            swept.DomainEvents.Select(domainEvent => domainEvent.GetType().Name));

        var sweptRelease = Assert.IsType<SeatReleased>(swept.DomainEvents[0]);
        var lazyRelease = Assert.IsType<SeatReleased>(lazy.DomainEvents[0]);
        Assert.Equal(lazyRelease.ClientId, sweptRelease.ClientId);
        Assert.Equal(lazyRelease.Reason, sweptRelease.Reason);
    }

    // -- utcNow must be UTC -----------------------------------------------

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Hold_WhenUtcNowIsNotUtc_ShouldThrow(DateTimeKind kind)
    {
        var seat = Available();

        var ex = Assert.Throws<ArgumentException>(() => seat.Hold(ClientA, NotUtc(kind)));

        Assert.Equal("utcNow", ex.ParamName);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Release_WhenUtcNowIsNotUtc_ShouldThrow(DateTimeKind kind)
    {
        var seat = HeldBy(ClientA, T0);

        var ex = Assert.Throws<ArgumentException>(() => seat.Release(ClientA, NotUtc(kind)));

        Assert.Equal("utcNow", ex.ParamName);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Sell_WhenUtcNowIsNotUtc_ShouldThrow(DateTimeKind kind)
    {
        var seat = HeldBy(ClientA, T0);

        var ex = Assert.Throws<ArgumentException>(() => seat.Sell(ClientA, NotUtc(kind)));

        Assert.Equal("utcNow", ex.ParamName);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void ExpireHold_WhenUtcNowIsNotUtc_ShouldThrow(DateTimeKind kind)
    {
        var seat = HeldBy(ClientA, T0);

        var ex = Assert.Throws<ArgumentException>(() => seat.ExpireHold(NotUtc(kind)));

        Assert.Equal("utcNow", ex.ParamName);
    }

    private static DateTime NotUtc(DateTimeKind kind) =>
        DateTime.SpecifyKind(WithinHold, kind);
}
