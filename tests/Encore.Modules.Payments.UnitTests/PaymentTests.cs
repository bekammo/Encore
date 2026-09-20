using Encore.Modules.Payments.Models;

namespace Encore.Modules.Payments.UnitTests;

/// <summary>
/// The payment state machine: what each transition permits, what it refuses, and
/// the two transitions that are deliberately idempotent. Time is passed in rather
/// than read, as it is for <c>Seat</c>, so nothing here touches a clock or a
/// database.
/// </summary>
/// <remarks>
/// The shape of this class is <c>DECISIONS.md</c> 029: <see cref="Payment"/> gets
/// factory construction and guarded transitions because it has genuine single-row
/// invariants, while staying in a flat module with no ports and no separate domain
/// assembly. These tests are what make "only an authorised payment may be
/// captured" a fact rather than a comment.
/// </remarks>
public class PaymentTests
{
    private static readonly Guid PaymentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrderId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ClientId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private const decimal Amount = 120.50m;
    private const string Currency = "GBP";
    private const string Key = "order-22222222-attempt-1";
    private const string GatewayReference = "auth_7f3c9a";

    /// <summary>Arbitrary fixed instant. Everything else is expressed relative to it.</summary>
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Later = T0.AddSeconds(3);

    private static Payment Pending() =>
        Payment.Create(PaymentId, OrderId, ClientId, Amount, Currency, Key, T0);

    private static Payment Authorized()
    {
        var payment = Pending();
        payment.Authorize(GatewayReference, Later);
        return payment;
    }

    private static Payment Captured()
    {
        var payment = Authorized();
        payment.Capture(Later);
        return payment;
    }

    private static Payment Declined()
    {
        var payment = Pending();
        payment.Decline(Later);
        return payment;
    }

    private static Payment TimedOut()
    {
        var payment = Pending();
        payment.TimeOut(Later);
        return payment;
    }

    private static Payment Voided()
    {
        var payment = Authorized();
        payment.Void(Later);
        return payment;
    }

    private static Payment InStatus(PaymentStatus status) => status switch
    {
        PaymentStatus.Pending => Pending(),
        PaymentStatus.Authorized => Authorized(),
        PaymentStatus.Captured => Captured(),
        PaymentStatus.Declined => Declined(),
        PaymentStatus.TimedOut => TimedOut(),
        PaymentStatus.Voided => Voided(),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped status.")
    };

    // -- Create -----------------------------------------------------------

    [Fact]
    public void Create_ShouldStartPending()
    {
        var payment = Pending();

        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Null(payment.GatewayReference);
        Assert.Null(payment.ResolvedAt);
    }

    [Fact]
    public void Create_ShouldRecordWhatIsOwedAndWhoOwesIt()
    {
        var payment = Pending();

        Assert.Equal(OrderId, payment.OrderId);
        Assert.Equal(ClientId, payment.ClientId);
        Assert.Equal(Amount, payment.Amount);
        Assert.Equal(Currency, payment.Currency);
        Assert.Equal(Key, payment.IdempotencyKey);
        Assert.Equal(T0, payment.AttemptedAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_WhenAmountIsNotPositive_ShouldThrow(int amount) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Payment.Create(PaymentId, OrderId, ClientId, amount, Currency, Key, T0));

    [Theory]
    [InlineData("")]
    [InlineData("GB")]
    [InlineData("GBPX")]
    public void Create_WhenCurrencyIsNotAThreeLetterCode_ShouldThrow(string currency) =>
        Assert.Throws<ArgumentException>(() =>
            Payment.Create(PaymentId, OrderId, ClientId, Amount, currency, Key, T0));

    [Fact]
    public void Create_WhenIdempotencyKeyIsBlank_ShouldThrow() =>
        Assert.Throws<ArgumentException>(() =>
            Payment.Create(PaymentId, OrderId, ClientId, Amount, Currency, "  ", T0));

    // -- Authorize --------------------------------------------------------

    [Fact]
    public void Authorize_WhenPending_ShouldHoldTheFunds()
    {
        var payment = Pending();

        payment.Authorize(GatewayReference, Later);

        Assert.Equal(PaymentStatus.Authorized, payment.Status);
        Assert.Equal(GatewayReference, payment.GatewayReference);
    }

    /// <summary>
    /// An authorisation is not an ending. <c>ResolvedAt</c> marks the point after
    /// which nothing more can happen to this attempt, and funds being held is
    /// precisely the state where something still can — a capture or a void.
    /// </summary>
    [Fact]
    public void Authorize_ShouldNotResolveTheAttempt()
    {
        var payment = Authorized();

        Assert.Null(payment.ResolvedAt);
        Assert.True(payment.IsLive);
    }

    [Fact]
    public void Authorize_WhenAlreadyAuthorized_ShouldRefuse()
    {
        var payment = Authorized();

        var ex = Assert.Throws<PaymentTransitionException>(
            () => payment.Authorize(GatewayReference, Later));

        Assert.Equal(PaymentTransitionReason.NotPending, ex.Reason);
        Assert.Equal(PaymentId, ex.PaymentId);
    }

    [Fact]
    public void Authorize_WhenDeclined_ShouldRefuse()
    {
        var payment = Declined();

        var ex = Assert.Throws<PaymentTransitionException>(
            () => payment.Authorize(GatewayReference, Later));

        Assert.Equal(PaymentTransitionReason.NotPending, ex.Reason);
    }

    // -- Decline ----------------------------------------------------------

    [Fact]
    public void Decline_WhenPending_ShouldResolveTheAttempt()
    {
        var payment = Pending();

        payment.Decline(Later);

        Assert.Equal(PaymentStatus.Declined, payment.Status);
        Assert.Equal(Later, payment.ResolvedAt);
    }

    /// <summary>
    /// A decline is the one ending that definitively moved no money, which is why
    /// it does not hold the order's one live-attempt slot: the customer may try
    /// again with a different card, and that is a new attempt with a new key.
    /// See <c>DECISIONS.md</c> 030.
    /// </summary>
    [Fact]
    public void Decline_ShouldNotLeaveTheAttemptLive()
    {
        var payment = Declined();

        Assert.False(payment.IsLive);
    }

    [Fact]
    public void Decline_WhenAuthorized_ShouldRefuse()
    {
        var payment = Authorized();

        var ex = Assert.Throws<PaymentTransitionException>(() => payment.Decline(Later));

        Assert.Equal(PaymentTransitionReason.NotPending, ex.Reason);
    }

    // -- TimeOut ----------------------------------------------------------

    [Fact]
    public void TimeOut_WhenPending_ShouldResolveTheAttempt()
    {
        var payment = Pending();

        payment.TimeOut(Later);

        Assert.Equal(PaymentStatus.TimedOut, payment.Status);
        Assert.Equal(Later, payment.ResolvedAt);
    }

    /// <summary>
    /// The load-bearing half of <c>DECISIONS.md</c> 031. A timed-out authorisation
    /// may or may not be holding funds, so the attempt stays live and keeps its
    /// key — the key is the only thing that makes the retry safe against a gateway
    /// that did receive the first call.
    /// </summary>
    [Fact]
    public void TimeOut_ShouldStayLiveAndKeepTheIdempotencyKey()
    {
        var payment = TimedOut();

        Assert.True(payment.IsLive);
        Assert.Equal(Key, payment.IdempotencyKey);
    }

    /// <summary>
    /// A capture that times out leaves the payment
    /// <see cref="PaymentStatus.Authorized"/>, because that is still exactly what
    /// is true: the funds are held and nothing has captured them. Only an
    /// authorisation can time out into <see cref="PaymentStatus.TimedOut"/>.
    /// </summary>
    [Fact]
    public void TimeOut_WhenAuthorized_ShouldRefuse()
    {
        var payment = Authorized();

        var ex = Assert.Throws<PaymentTransitionException>(() => payment.TimeOut(Later));

        Assert.Equal(PaymentTransitionReason.NotPending, ex.Reason);
    }

    // -- Retry ------------------------------------------------------------

    [Fact]
    public void Retry_WhenTimedOut_ShouldBecomePendingAgain()
    {
        var payment = TimedOut();

        payment.Retry(Later.AddSeconds(30));

        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Null(payment.ResolvedAt);
        Assert.Equal(Later.AddSeconds(30), payment.AttemptedAt);
    }

    /// <summary>
    /// The retry reuses the row, and therefore the key. A second row would need a
    /// second key, and a second key against a gateway that did receive the first
    /// call is a double authorisation.
    /// </summary>
    [Fact]
    public void Retry_ShouldKeepTheIdempotencyKey()
    {
        var payment = TimedOut();

        payment.Retry(Later.AddSeconds(30));

        Assert.Equal(Key, payment.IdempotencyKey);
        Assert.Equal(Amount, payment.Amount);
    }

    [Theory]
    [InlineData(PaymentStatus.Pending)]
    [InlineData(PaymentStatus.Authorized)]
    [InlineData(PaymentStatus.Captured)]
    [InlineData(PaymentStatus.Declined)]
    [InlineData(PaymentStatus.Voided)]
    public void Retry_WhenNotTimedOut_ShouldRefuse(PaymentStatus status)
    {
        var payment = InStatus(status);

        var ex = Assert.Throws<PaymentTransitionException>(() => payment.Retry(Later));

        Assert.Equal(PaymentTransitionReason.NotTimedOut, ex.Reason);
    }

    // -- Capture ----------------------------------------------------------

    [Fact]
    public void Capture_WhenAuthorized_ShouldTakeTheMoney()
    {
        var payment = Authorized();

        payment.Capture(Later.AddSeconds(1));

        Assert.Equal(PaymentStatus.Captured, payment.Status);
        Assert.Equal(Later.AddSeconds(1), payment.ResolvedAt);
    }

    /// <summary>
    /// Idempotent for the same reason re-holding a seat is (<c>DECISIONS.md</c>
    /// 007): a retried confirm after a dropped response must not tell a customer
    /// their completed payment failed. The resolution time deliberately does not
    /// move.
    /// </summary>
    [Fact]
    public void Capture_WhenAlreadyCaptured_ShouldBeANoOp()
    {
        var payment = Authorized();
        payment.Capture(Later);

        payment.Capture(Later.AddMinutes(5));

        Assert.Equal(PaymentStatus.Captured, payment.Status);
        Assert.Equal(Later, payment.ResolvedAt);
    }

    [Theory]
    [InlineData(PaymentStatus.Pending)]
    [InlineData(PaymentStatus.Declined)]
    [InlineData(PaymentStatus.TimedOut)]
    [InlineData(PaymentStatus.Voided)]
    public void Capture_WithoutALiveAuthorization_ShouldRefuse(PaymentStatus status)
    {
        var payment = InStatus(status);

        var ex = Assert.Throws<PaymentTransitionException>(() => payment.Capture(Later));

        Assert.Equal(PaymentTransitionReason.NotAuthorized, ex.Reason);
    }

    // -- Void -------------------------------------------------------------

    [Fact]
    public void Void_WhenAuthorized_ShouldReleaseTheFunds()
    {
        var payment = Authorized();

        payment.Void(Later.AddSeconds(1));

        Assert.Equal(PaymentStatus.Voided, payment.Status);
        Assert.Equal(Later.AddSeconds(1), payment.ResolvedAt);
        Assert.False(payment.IsLive);
    }

    [Fact]
    public void Void_WhenAlreadyVoided_ShouldBeANoOp()
    {
        var payment = Authorized();
        payment.Void(Later);

        payment.Void(Later.AddMinutes(5));

        Assert.Equal(PaymentStatus.Voided, payment.Status);
        Assert.Equal(Later, payment.ResolvedAt);
    }

    /// <summary>
    /// Captured is terminal. Undoing it is a refund, which is a real operation
    /// this system does not have, and pretending otherwise here would let a caller
    /// believe money had come back when nothing had moved.
    /// </summary>
    [Fact]
    public void Void_WhenCaptured_ShouldRefuse()
    {
        var payment = Captured();

        var ex = Assert.Throws<PaymentTransitionException>(() => payment.Void(Later));

        Assert.Equal(PaymentTransitionReason.AlreadyCaptured, ex.Reason);
    }

    [Theory]
    [InlineData(PaymentStatus.Pending)]
    [InlineData(PaymentStatus.Declined)]
    [InlineData(PaymentStatus.TimedOut)]
    public void Void_WithoutALiveAuthorization_ShouldRefuse(PaymentStatus status)
    {
        var payment = InStatus(status);

        var ex = Assert.Throws<PaymentTransitionException>(() => payment.Void(Later));

        Assert.Equal(PaymentTransitionReason.NotAuthorized, ex.Reason);
    }

    // -- Liveness ---------------------------------------------------------

    /// <summary>
    /// The set this property describes is the set the partial unique index filters
    /// on, and the two are pinned to each other by <c>PaymentConfiguration</c>'s
    /// SQL literal, which no compiler checks. If this table changes, that filter
    /// has to change with it.
    /// </summary>
    [Theory]
    [InlineData(PaymentStatus.Pending, true)]
    [InlineData(PaymentStatus.Authorized, true)]
    [InlineData(PaymentStatus.Captured, true)]
    [InlineData(PaymentStatus.TimedOut, true)]
    [InlineData(PaymentStatus.Declined, false)]
    [InlineData(PaymentStatus.Voided, false)]
    public void IsLive_ShouldMatchTheIndexFilter(PaymentStatus status, bool expected) =>
        Assert.Equal(expected, InStatus(status).IsLive);

    // -- Identity ---------------------------------------------------------

    /// <summary>
    /// DECISIONS 045, which is 038 applied to this type. 029's case for the
    /// factory is that a payment cannot be conjured into a state no rule approved,
    /// and an empty Guid is exactly such a state reached through that door: an
    /// attempt with no id cannot be addressed and collides on the primary key with
    /// the next one, an attempt against no order has nothing to be a payment for,
    /// and one owed by nobody cannot be scoped to a caller — which is the check
    /// every read path in this module makes.
    /// </summary>
    [Fact]
    public void Create_WhenIdIsEmpty_ShouldThrow()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            Payment.Create(Guid.Empty, OrderId, ClientId, Amount, Currency, Key, T0));

        Assert.Equal("id", exception.ParamName);
    }

    [Fact]
    public void Create_WhenOrderIdIsEmpty_ShouldThrow()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            Payment.Create(PaymentId, Guid.Empty, ClientId, Amount, Currency, Key, T0));

        Assert.Equal("orderId", exception.ParamName);
    }

    [Fact]
    public void Create_WhenClientIdIsEmpty_ShouldThrow()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            Payment.Create(PaymentId, OrderId, Guid.Empty, Amount, Currency, Key, T0));

        Assert.Equal("clientId", exception.ParamName);
    }

    // -- utcNow must be UTC -------------------------------------------------

    /// <summary>
    /// DECISIONS 045, which is 039 applied to this type. Every method here takes
    /// the current instant as a parameter, which makes "this is a UTC instant" a
    /// precondition of each of them rather than a convention upstream.
    /// <see cref="DateTimeKind.Unspecified"/> is refused alongside
    /// <see cref="DateTimeKind.Local"/>: a wall clock with no zone is a different
    /// instant in London and in Los Angeles, so treating it as UTC would be a guess.
    /// </summary>
    /// <remarks>
    /// Before these guards the mistake surfaced at the Npgsql boundary, several
    /// layers from the caller that made it, as a provider error about a
    /// timestamptz — and only for the two fields that reach a column. Now it
    /// surfaces here, naming the parameter.
    /// </remarks>
    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Create_WhenUtcNowIsNotUtc_ShouldThrow(DateTimeKind kind)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            Payment.Create(PaymentId, OrderId, ClientId, Amount, Currency, Key, NotUtc(kind)));

        Assert.Equal("utcNow", exception.ParamName);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Authorize_WhenUtcNowIsNotUtc_ShouldThrow(DateTimeKind kind)
    {
        var payment = Pending();

        var exception = Assert.Throws<ArgumentException>(() =>
            payment.Authorize(GatewayReference, NotUtc(kind)));

        Assert.Equal("utcNow", exception.ParamName);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Decline_WhenUtcNowIsNotUtc_ShouldThrow(DateTimeKind kind)
    {
        var payment = Pending();

        var exception = Assert.Throws<ArgumentException>(() => payment.Decline(NotUtc(kind)));

        Assert.Equal("utcNow", exception.ParamName);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void TimeOut_WhenUtcNowIsNotUtc_ShouldThrow(DateTimeKind kind)
    {
        var payment = Pending();

        var exception = Assert.Throws<ArgumentException>(() => payment.TimeOut(NotUtc(kind)));

        Assert.Equal("utcNow", exception.ParamName);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Retry_WhenUtcNowIsNotUtc_ShouldThrow(DateTimeKind kind)
    {
        var payment = TimedOut();

        var exception = Assert.Throws<ArgumentException>(() => payment.Retry(NotUtc(kind)));

        Assert.Equal("utcNow", exception.ParamName);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Capture_WhenUtcNowIsNotUtc_ShouldThrow(DateTimeKind kind)
    {
        var payment = Authorized();

        var exception = Assert.Throws<ArgumentException>(() => payment.Capture(NotUtc(kind)));

        Assert.Equal("utcNow", exception.ParamName);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Void_WhenUtcNowIsNotUtc_ShouldThrow(DateTimeKind kind)
    {
        var payment = Authorized();

        var exception = Assert.Throws<ArgumentException>(() => payment.Void(NotUtc(kind)));

        Assert.Equal("utcNow", exception.ParamName);
    }

    /// <summary>
    /// The two idempotent transitions guard the instant <i>before</i> their early
    /// return, which is the ordering <c>Seat.Hold</c> uses and the only one worth
    /// having: a caller passing a local clock has the same bug whether or not the
    /// attempt happens to be settled already. Guarding after the return would
    /// report that bug only sometimes, depending on state the caller cannot see.
    /// </summary>
    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Capture_WhenAlreadyCaptured_ShouldStillRejectANonUtcInstant(DateTimeKind kind)
    {
        var payment = Captured();

        var exception = Assert.Throws<ArgumentException>(() => payment.Capture(NotUtc(kind)));

        Assert.Equal("utcNow", exception.ParamName);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Void_WhenAlreadyVoided_ShouldStillRejectANonUtcInstant(DateTimeKind kind)
    {
        var payment = Voided();

        var exception = Assert.Throws<ArgumentException>(() => payment.Void(NotUtc(kind)));

        Assert.Equal("utcNow", exception.ParamName);
    }

    /// <summary>
    /// The same wall-clock reading as <see cref="Later"/>, wearing the wrong Kind.
    /// Same numbers, so a test that fails does so because of the Kind and nothing
    /// else.
    /// </summary>
    private static DateTime NotUtc(DateTimeKind kind) => DateTime.SpecifyKind(Later, kind);
}
