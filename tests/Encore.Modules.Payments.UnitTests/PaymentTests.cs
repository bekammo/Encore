using Encore.Modules.Payments.Models;

namespace Encore.Modules.Payments.UnitTests;

/// <summary>
/// The payment state machine: what each transition permits and refuses, and the two
/// idempotent ones. Time is passed in; no clock, no database.
/// </summary>
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

    /// <summary>Later still: when reconciliation asked the gateway what happened.</summary>
    private static readonly DateTime MuchLater = T0.AddMinutes(10);

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

    private static Payment Abandoned()
    {
        var payment = TimedOut();
        payment.ResolveAsAbandoned(MuchLater);
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
        PaymentStatus.Abandoned => Abandoned(),
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

    /// <summary>An authorisation is not an ending: a capture or void is still to come.</summary>
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

    /// <summary>A decline moved no money, so it does not hold the order's live slot.</summary>
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

    /// <summary>A timed-out authorisation stays live and keeps its key.</summary>
    [Fact]
    public void TimeOut_ShouldStayLiveAndKeepTheIdempotencyKey()
    {
        var payment = TimedOut();

        Assert.True(payment.IsLive);
        Assert.Equal(Key, payment.IdempotencyKey);
    }

    /// <summary>Only an authorisation can time out into TimedOut; a capture timeout stays Authorized.</summary>
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

    /// <summary>The retry reuses the row, and therefore the key.</summary>
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

    // -- Reconciliation ---------------------------------------------------

    /// <summary>
    /// Reconciliation transitions record an answer looked up at the gateway. Separate methods,
    /// so the ordinary path cannot write an answer it never received.
    /// </summary>
    [Fact]
    public void ResolveAsVoided_WhenTimedOut_ShouldReleaseTheFundsItFound()
    {
        var payment = TimedOut();

        payment.ResolveAsVoided(GatewayReference, MuchLater);

        Assert.Equal(PaymentStatus.Voided, payment.Status);
        Assert.Equal(MuchLater, payment.ResolvedAt);
        Assert.False(payment.IsLive);
    }

    /// <summary>The looked-up reference is recorded, for anyone chasing the payment.</summary>
    [Fact]
    public void ResolveAsVoided_ShouldRecordTheReferenceItFound()
    {
        var payment = TimedOut();

        Assert.Null(payment.GatewayReference);

        payment.ResolveAsVoided(GatewayReference, MuchLater);

        Assert.Equal(GatewayReference, payment.GatewayReference);
    }

    /// <summary>AttemptedAt does not move: the funds were held when the original call arrived.</summary>
    [Fact]
    public void ResolveAsVoided_ShouldNotMoveTheAttemptTime()
    {
        var payment = TimedOut();

        payment.ResolveAsVoided(GatewayReference, MuchLater);

        Assert.Equal(T0, payment.AttemptedAt);
    }

    [Fact]
    public void ResolveAsVoided_WithoutAReference_ShouldThrow()
    {
        var payment = TimedOut();

        Assert.Throws<ArgumentException>(() => payment.ResolveAsVoided(" ", MuchLater));
    }

    [Theory]
    [InlineData(PaymentStatus.Pending)]
    [InlineData(PaymentStatus.Authorized)]
    [InlineData(PaymentStatus.Captured)]
    [InlineData(PaymentStatus.Declined)]
    [InlineData(PaymentStatus.Voided)]
    public void ResolveAsVoided_WhenNotTimedOut_ShouldRefuse(PaymentStatus status)
    {
        var payment = InStatus(status);

        var ex = Assert.Throws<PaymentTransitionException>(
            () => payment.ResolveAsVoided(GatewayReference, MuchLater));

        Assert.Equal(PaymentTransitionReason.NotTimedOut, ex.Reason);
    }

    /// <summary>A refusal read back from the gateway settles the attempt as Declined.</summary>
    [Fact]
    public void ResolveAsDeclined_WhenTimedOut_ShouldResolveTheAttempt()
    {
        var payment = TimedOut();

        payment.ResolveAsDeclined(MuchLater);

        Assert.Equal(PaymentStatus.Declined, payment.Status);
        Assert.Equal(MuchLater, payment.ResolvedAt);
        Assert.False(payment.IsLive);
    }

    [Theory]
    [InlineData(PaymentStatus.Pending)]
    [InlineData(PaymentStatus.Authorized)]
    [InlineData(PaymentStatus.Captured)]
    [InlineData(PaymentStatus.Declined)]
    [InlineData(PaymentStatus.Voided)]
    public void ResolveAsDeclined_WhenNotTimedOut_ShouldRefuse(PaymentStatus status)
    {
        var payment = InStatus(status);

        var ex = Assert.Throws<PaymentTransitionException>(() => payment.ResolveAsDeclined(MuchLater));

        Assert.Equal(PaymentTransitionReason.NotTimedOut, ex.Reason);
    }

    /// <summary>No record at the gateway: Abandoned, not live, freeing the order's slot.</summary>
    [Fact]
    public void ResolveAsAbandoned_WhenTimedOut_ShouldResolveTheAttempt()
    {
        var payment = TimedOut();

        payment.ResolveAsAbandoned(MuchLater);

        Assert.Equal(PaymentStatus.Abandoned, payment.Status);
        Assert.Equal(MuchLater, payment.ResolvedAt);
        Assert.False(payment.IsLive);
    }

    /// <summary>Nothing reached the gateway, so no reference is recorded.</summary>
    [Fact]
    public void ResolveAsAbandoned_ShouldLeaveNoGatewayReference()
    {
        var payment = TimedOut();

        payment.ResolveAsAbandoned(MuchLater);

        Assert.Null(payment.GatewayReference);
    }

    [Theory]
    [InlineData(PaymentStatus.Pending)]
    [InlineData(PaymentStatus.Authorized)]
    [InlineData(PaymentStatus.Captured)]
    [InlineData(PaymentStatus.Declined)]
    [InlineData(PaymentStatus.Voided)]
    public void ResolveAsAbandoned_WhenNotTimedOut_ShouldRefuse(PaymentStatus status)
    {
        var payment = InStatus(status);

        var ex = Assert.Throws<PaymentTransitionException>(() => payment.ResolveAsAbandoned(MuchLater));

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

    /// <summary>Capture is idempotent, and the resolution time does not move.</summary>
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

    /// <summary>A captured payment cannot be voided; that would be a refund.</summary>
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

    /// <summary>The live statuses match the SQL filter of the partial unique index.</summary>
    [Theory]
    [InlineData(PaymentStatus.Pending, true)]
    [InlineData(PaymentStatus.Authorized, true)]
    [InlineData(PaymentStatus.Captured, true)]
    [InlineData(PaymentStatus.TimedOut, true)]
    [InlineData(PaymentStatus.Declined, false)]
    [InlineData(PaymentStatus.Voided, false)]
    [InlineData(PaymentStatus.Abandoned, false)]
    public void IsLive_ShouldMatchTheIndexFilter(PaymentStatus status, bool expected) =>
        Assert.Equal(expected, InStatus(status).IsLive);

    // -- Identity ---------------------------------------------------------

    /// <summary>Empty ids are refused at construction.</summary>
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

    /// <summary>Every transition requires a UTC instant; <c>Local</c> and <c>Unspecified</c> are refused.</summary>
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
    /// The idempotent transitions check the instant before their early return, so a bad clock is
    /// reported regardless of state.
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

    /// <summary>The reconciliation transitions have the same UTC precondition.</summary>
    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void ResolveAsVoided_WhenUtcNowIsNotUtc_ShouldThrow(DateTimeKind kind)
    {
        var payment = TimedOut();

        var exception = Assert.Throws<ArgumentException>(
            () => payment.ResolveAsVoided(GatewayReference, NotUtc(kind)));

        Assert.Equal("utcNow", exception.ParamName);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void ResolveAsDeclined_WhenUtcNowIsNotUtc_ShouldThrow(DateTimeKind kind)
    {
        var payment = TimedOut();

        var exception = Assert.Throws<ArgumentException>(() => payment.ResolveAsDeclined(NotUtc(kind)));

        Assert.Equal("utcNow", exception.ParamName);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void ResolveAsAbandoned_WhenUtcNowIsNotUtc_ShouldThrow(DateTimeKind kind)
    {
        var payment = TimedOut();

        var exception = Assert.Throws<ArgumentException>(() => payment.ResolveAsAbandoned(NotUtc(kind)));

        Assert.Equal("utcNow", exception.ParamName);
    }

    /// <summary>The same wall-clock reading as <see cref="Later"/> with the wrong Kind.</summary>
    private static DateTime NotUtc(DateTimeKind kind) => DateTime.SpecifyKind(Later, kind);
}
