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

    /// <summary>
    /// The exact instant a hold taken at <see cref="T0"/> expires. DECISIONS 007
    /// settles the boundary as exclusive — a hold at exactly this moment is over
    /// — and until DECISIONS 040 nothing tested either side of it: WithinHold and
    /// AfterHold both sit a comfortable minute away.
    /// </summary>
    /// <remarks>
    /// Written as AddMinutes(5) rather than T0 + Seat.HoldDuration, matching the
    /// rest of this file and for the same reason: a test that reads the constant
    /// cannot catch the constant changing.
    /// </remarks>
    private static readonly DateTime AtExpiry = T0.AddMinutes(5);

    /// <summary>
    /// One tick before expiry: the last instant at which the hold is still live.
    /// </summary>
    /// <remarks>
    /// A tick is 100ns, below the microsecond Postgres stores. That is fine here
    /// because nothing in this file round-trips through a database, and it is
    /// deliberately not a trick to copy into the integration suite, which
    /// truncates its instants for exactly that reason.
    /// </remarks>
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

    /// <summary>
    /// The postcondition DECISIONS 005 spends its length arguing for, asserted
    /// rather than assumed. Every seat is born Available with no holder and no
    /// expiry, and the only routes out are the transition methods.
    /// </summary>
    [Fact]
    public void Create_ShouldProduceAnAvailableSeatWithNoHolderAndNoExpiry()
    {
        var seat = Seat.Create(SeatId, EventId);

        Assert.Equal(SeatId, seat.Id);
        Assert.Equal(EventId, seat.EventId);
        Assert.Equal(SeatStatus.Available, seat.Status);
        Assert.Null(seat.HeldByClientId);
        Assert.Null(seat.HoldExpiresAt);

        // Creation raises nothing. A deliberate absence — there is no SeatCreated
        // — and worth pinning before the outbox arrives and makes every raised
        // event something that leaves the process.
        Assert.Empty(seat.DomainEvents);
    }

    [Fact]
    public void Create_WhenIdIsEmpty_ShouldThrow()
    {
        var exception = Assert.Throws<ArgumentException>(() => Seat.Create(Guid.Empty, EventId));

        Assert.Equal("id", exception.ParamName);
    }

    [Fact]
    public void Create_WhenEventIdIsEmpty_ShouldThrow()
    {
        var exception = Assert.Throws<ArgumentException>(() => Seat.Create(SeatId, Guid.Empty));

        Assert.Equal("eventId", exception.ParamName);
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

    /// <summary>
    /// The edge DECISIONS 007 left open and 040 settles: a client re-holding a
    /// seat after their <i>own</i> hold lapsed reclaims it, rather than being
    /// refused for squatting.
    /// </summary>
    /// <remarks>
    /// Asserting the two events by type and order, not merely counting them, is
    /// the point of this test. A count of two is satisfied by any pair, and the
    /// question here is precisely <i>which</i> of the two overlapping rules wins:
    /// the client is released from their own lapsed hold and then granted a new
    /// one, rather than the re-hold being folded into a silent no-op the way an
    /// unexpired re-hold is.
    /// </remarks>
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
            e =>
            {
                var released = Assert.IsType<SeatReleased>(e);
                Assert.Equal(ClientA, released.ClientId);
                Assert.Equal(SeatReleaseReason.Expired, released.Reason);
                Assert.Equal(AfterHold, released.OccurredAt);
            },
            e =>
            {
                var held = Assert.IsType<SeatHeld>(e);
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

    /// <summary>
    /// "No-op" means literally nothing changed, which is less obvious than it
    /// sounds: the natural guess is that Release tidies the stale columns on its
    /// way past.
    /// </summary>
    /// <remarks>
    /// It deliberately does not. The row still reads Held by ClientA with an
    /// expiry in the past, and the next Hold reclaims it lazily. Tidying here
    /// would give the release path an opinion about expiry, which is how the
    /// background sweep acquires authority the design says it must never have
    /// (DECISIONS 007, 040).
    /// </remarks>
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

    // -- The expiry boundary ----------------------------------------------

    /// <summary>
    /// The four tests below are the whole of DECISIONS 007's exclusive boundary,
    /// asserted for the first time. The rule lives in one comparison —
    /// <c>HoldExpiresAt &lt;= utcNow</c> — and flipping it to <c>&lt;</c> would
    /// change behaviour at exactly one instant, which is precisely the kind of
    /// change every other test in this file is a minute too far away to notice.
    /// </summary>
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

    // -- utcNow must be UTC -------------------------------------------------

    /// <summary>
    /// DECISIONS 039. The domain receives the clock as a parameter, which makes
    /// "this is a UTC instant" a precondition of these three methods rather than
    /// a convention somewhere upstream. Unspecified is refused alongside Local:
    /// a wall clock with no zone is a different instant in London and in Los
    /// Angeles, so treating it as UTC would be a guess.
    /// </summary>
    /// <remarks>
    /// Before this guard the mistake surfaced at the Npgsql boundary, several
    /// layers from the caller that made it, as a provider error about a
    /// timestamptz. Now it surfaces here, naming the parameter.
    /// </remarks>
    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Hold_WhenUtcNowIsNotUtc_ShouldThrow(DateTimeKind kind)
    {
        var seat = Available();

        var exception = Assert.Throws<ArgumentException>(() => seat.Hold(ClientA, NotUtc(kind)));

        Assert.Equal("utcNow", exception.ParamName);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Release_WhenUtcNowIsNotUtc_ShouldThrow(DateTimeKind kind)
    {
        var seat = HeldBy(ClientA, T0);

        var exception = Assert.Throws<ArgumentException>(() => seat.Release(ClientA, NotUtc(kind)));

        Assert.Equal("utcNow", exception.ParamName);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Sell_WhenUtcNowIsNotUtc_ShouldThrow(DateTimeKind kind)
    {
        var seat = HeldBy(ClientA, T0);

        var exception = Assert.Throws<ArgumentException>(() => seat.Sell(ClientA, NotUtc(kind)));

        Assert.Equal("utcNow", exception.ParamName);
    }

    /// <summary>
    /// The same wall-clock reading as <see cref="WithinHold"/>, wearing the wrong
    /// Kind. Same numbers, so a test that fails does so because of the Kind and
    /// nothing else.
    /// </summary>
    private static DateTime NotUtc(DateTimeKind kind) =>
        DateTime.SpecifyKind(WithinHold, kind);
}
