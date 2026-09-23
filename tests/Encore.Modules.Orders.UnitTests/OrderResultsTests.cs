using Encore.Modules.Inventory.Contracts;
using Encore.Modules.Orders.Endpoints;
using Encore.Modules.Orders.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Encore.Modules.Orders.UnitTests;

/// <summary>The outcome-to-HTTP mapping, tested without a host.</summary>
public class OrderResultsTests
{
    private static readonly Guid OrderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EventId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SeatId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid OtherSeatId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTime PlacedAt = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly PathString Path = new("/orders");

    private static int StatusOf(IResult result) => result switch
    {
        IStatusCodeHttpResult status => status.StatusCode
            ?? throw new InvalidOperationException("Result carries no status code."),
        _ => throw new InvalidOperationException($"Unexpected result type {result.GetType().Name}.")
    };

    private static string? ReasonOf(IResult result) =>
        result is ProblemHttpResult problem
        && problem.ProblemDetails.Extensions.TryGetValue("reason", out var reason)
            ? reason as string
            : null;

    private static bool? RetriableOf(IResult result) =>
        result is ProblemHttpResult problem
        && problem.ProblemDetails.Extensions.TryGetValue("retriable", out var retriable)
            ? retriable as bool?
            : null;

    private static Order AnOrder(OrderStatus status) => new()
    {
        Id = OrderId,
        ClientId = Guid.NewGuid(),
        EventId = EventId,
        Status = status,
        PlacedAt = PlacedAt,
        HoldsExpireAt = status is OrderStatus.Pending ? PlacedAt.AddMinutes(5) : null,
        ClosedAt = status is OrderStatus.Pending ? null : PlacedAt,
        Total = 50m,
        Currency = "GBP",
        Lines = [new OrderLine { Id = Guid.NewGuid(), OrderId = OrderId, SeatId = SeatId, UnitPrice = 50m, Currency = "GBP" }]
    };

    // -- Checkout ---------------------------------------------------------

    [Fact]
    public void ForCheckout_WhenCreated_ShouldBe201WithALocation()
    {
        var result = OrderResults.ForCheckout(
            CheckoutResult.Created(AnOrder(OrderStatus.Pending)), Path);

        var created = Assert.IsType<Created<OrderResponse>>(result);
        Assert.Equal(StatusCodes.Status201Created, StatusOf(result));
        Assert.Equal($"/orders/{OrderId}", created.Location);
        Assert.Equal("pending", created.Value!.Status);
        Assert.Equal(SeatId, Assert.Single(created.Value.Lines).SeatId);
    }

    /// <summary>The 400s: what a client could have known was wrong before sending.</summary>
    [Theory]
    [InlineData(CheckoutOutcome.NoSeats, "no_seats")]
    [InlineData(CheckoutOutcome.DuplicateSeat, "duplicate_seat")]
    [InlineData(CheckoutOutcome.TooManySeats, "too_many_seats")]
    public void ForCheckout_MalformedRequests_ShouldBe400WithAReason(
        CheckoutOutcome outcome,
        string expectedReason)
    {
        var result = OrderResults.ForCheckout(CheckoutResult.Refused(outcome), Path);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Equal(expectedReason, ReasonOf(result));
    }

    /// <summary>The 409s: refusals about the state of the world.</summary>
    [Theory]
    [InlineData(CheckoutOutcome.EventNotFound, "event_not_found", false)]
    [InlineData(CheckoutOutcome.NotOnSale, "not_on_sale", true)]
    [InlineData(CheckoutOutcome.CheckoutAlreadyOpen, "checkout_already_open", false)]
    public void ForCheckout_Refusals_ShouldBe409WithAReasonAndRetriable(
        CheckoutOutcome outcome,
        string expectedReason,
        bool expectedRetriable)
    {
        var result = OrderResults.ForCheckout(CheckoutResult.Refused(outcome), Path);

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(result));
        Assert.Equal(expectedReason, ReasonOf(result));
        Assert.Equal(expectedRetriable, RetriableOf(result));
    }

    /// <summary>A missing event is 409, not 404: the addressed <c>/orders</c> exists.</summary>
    [Fact]
    public void ForCheckout_WhenEventMissing_ShouldNotBe404()
    {
        var result = OrderResults.ForCheckout(
            CheckoutResult.Refused(CheckoutOutcome.EventNotFound), Path);

        Assert.NotEqual(StatusCodes.Status404NotFound, StatusOf(result));
    }

    /// <summary>
    /// An open checkout is named, so a client whose 201 was lost can still finish or cancel it.
    /// </summary>
    [Fact]
    public void ForCheckout_WhenACheckoutIsAlreadyOpen_ShouldNameIt()
    {
        var result = OrderResults.ForCheckout(CheckoutResult.AlreadyOpen(OrderId), Path);

        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(result));
        Assert.Equal("checkout_already_open", ReasonOf(result));
        Assert.Equal(false, RetriableOf(result));
        Assert.Equal(OrderId, problem.ProblemDetails.Extensions["orderId"]);
    }

    /// <summary>Too many seats is a 400 that states the limit.</summary>
    [Fact]
    public void ForCheckout_WhenTooManySeats_ShouldReportTheLimit()
    {
        var result = OrderResults.ForCheckout(
            CheckoutResult.Refused(CheckoutOutcome.TooManySeats), Path);

        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(
            SeatReservationLimits.MaxHoldsPerClientPerEvent,
            problem.ProblemDetails.Extensions["limit"]);
    }

    // -- Checkout, seats unavailable --------------------------------------

    [Fact]
    public void ForCheckout_WhenSeatsUnavailable_ShouldNameEverySeatAndItsReason()
    {
        var result = OrderResults.ForCheckout(
            CheckoutResult.Unavailable(
            [
                new HoldSeatResponse(SeatId, HoldSeatStatus.AlreadyHeld),
                new HoldSeatResponse(OtherSeatId, HoldSeatStatus.AlreadySold)
            ]),
            Path);

        var problem = Assert.IsType<ProblemHttpResult>(result);

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(result));
        Assert.Equal("seats_unavailable", ReasonOf(result));

        var seats = Assert.IsAssignableFrom<System.Collections.IEnumerable>(
            problem.ProblemDetails.Extensions["seats"]);

        Assert.Equal(2, seats.Cast<object>().Count());
    }

    /// <summary>The top-level <c>retriable</c> is true only if every seat's refusal is.</summary>
    [Fact]
    public void ForCheckout_WhenEveryRefusalIsRetriable_ShouldBeRetriable()
    {
        var result = OrderResults.ForCheckout(
            CheckoutResult.Unavailable(
            [
                new HoldSeatResponse(SeatId, HoldSeatStatus.AlreadyHeld),
                new HoldSeatResponse(OtherSeatId, HoldSeatStatus.LostRace)
            ]),
            Path);

        Assert.True(RetriableOf(result));
    }

    [Fact]
    public void ForCheckout_WhenAnyRefusalIsTerminal_ShouldNotBeRetriable()
    {
        var result = OrderResults.ForCheckout(
            CheckoutResult.Unavailable(
            [
                new HoldSeatResponse(SeatId, HoldSeatStatus.LostRace),
                new HoldSeatResponse(OtherSeatId, HoldSeatStatus.AlreadySold)
            ]),
            Path);

        Assert.False(RetriableOf(result));
    }

    /// <summary>Every refusal Inventory can report maps to something.</summary>
    [Theory]
    [InlineData(HoldSeatStatus.AlreadyHeld)]
    [InlineData(HoldSeatStatus.AlreadySold)]
    [InlineData(HoldSeatStatus.SeatNotFound)]
    [InlineData(HoldSeatStatus.LostRace)]
    [InlineData(HoldSeatStatus.HoldCapReached)]
    [InlineData(HoldSeatStatus.ConcurrentRequestInFlight)]
    public void ForCheckout_EveryHoldRefusal_ShouldMap(HoldSeatStatus status)
    {
        var result = OrderResults.ForCheckout(
            CheckoutResult.Unavailable([new HoldSeatResponse(SeatId, status)]), Path);

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(result));
    }

    // -- Confirm ----------------------------------------------------------

    [Fact]
    public void ForConfirm_WhenConfirmed_ShouldBe200()
    {
        var result = OrderResults.ForConfirm(
            new OrderActionResult(OrderActionOutcome.Completed, AnOrder(OrderStatus.Confirmed)),
            Path);

        var ok = Assert.IsType<Ok<OrderResponse>>(result);
        Assert.Equal("confirmed", ok.Value!.Status);
    }

    /// <summary>A confirm whose holds lapsed still completed; the order says Expired.</summary>
    [Theory]
    [InlineData(OrderStatus.Expired, "holds_expired")]
    [InlineData(OrderStatus.Failed, "order_failed")]
    public void ForConfirm_WhenTheOrderEndedBadly_ShouldBe409WithTheReason(
        OrderStatus status,
        string expectedReason)
    {
        var result = OrderResults.ForConfirm(
            new OrderActionResult(OrderActionOutcome.Completed, AnOrder(status)), Path);

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(result));
        Assert.Equal(expectedReason, ReasonOf(result));
        Assert.False(RetriableOf(result));
    }

    [Fact]
    public void ForConfirm_WhenOrderMissing_ShouldBe404()
    {
        var result = OrderResults.ForConfirm(
            new OrderActionResult(OrderActionOutcome.OrderNotFound), Path);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
        Assert.Equal("order_not_found", ReasonOf(result));
    }

    [Fact]
    public void ForConfirm_WhenAlreadyEnded_ShouldBe409AndSayWhichEnding()
    {
        var result = OrderResults.ForConfirm(
            new OrderActionResult(OrderActionOutcome.NotPending, AnOrder(OrderStatus.Cancelled)),
            Path);

        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(result));
        Assert.Equal("order_not_pending", ReasonOf(result));
        Assert.Contains("cancelled", problem.ProblemDetails.Detail);
    }

    /// <summary>AwaitingCapture is a 200: the customer has every seat.</summary>
    [Fact]
    public void ForConfirm_WhenAwaitingCapture_ShouldBe200WithTheStatusSaidPlainly()
    {
        var result = OrderResults.ForConfirm(
            new OrderActionResult(OrderActionOutcome.Completed, AnOrder(OrderStatus.AwaitingCapture)),
            Path);

        var ok = Assert.IsType<Ok<OrderResponse>>(result);
        Assert.Equal("awaiting_capture", ok.Value!.Status);
    }

    /// <summary>Payment failures are retriable, with a different card if need be.</summary>
    [Theory]
    [InlineData(OrderActionOutcome.PaymentDeclined, "payment_declined")]
    [InlineData(OrderActionOutcome.PaymentTimedOut, "payment_timed_out")]
    public void ForConfirm_WhenThePaymentDidNotGoThrough_ShouldBe409AndRetriable(
        OrderActionOutcome outcome,
        string expectedReason)
    {
        var result = OrderResults.ForConfirm(
            new OrderActionResult(outcome, AnOrder(OrderStatus.Pending)), Path);

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(result));
        Assert.Equal(expectedReason, ReasonOf(result));
        Assert.True(RetriableOf(result));
    }

    /// <summary>The status in the message is spelled as the body spells it.</summary>
    [Fact]
    public void ForCancel_WhenAwaitingCapture_ShouldSayTheStatusTheSameWayTheBodyDoes()
    {
        var result = OrderResults.ForCancel(
            new OrderActionResult(OrderActionOutcome.NotPending, AnOrder(OrderStatus.AwaitingCapture)),
            Path);

        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(result));
        Assert.Contains("awaiting_capture", problem.ProblemDetails.Detail);
    }

    [Fact]
    public void ForConfirm_WhenLostRace_ShouldBe409AndRetriable()
    {
        var result = OrderResults.ForConfirm(
            new OrderActionResult(OrderActionOutcome.LostRace, AnOrder(OrderStatus.Pending)),
            Path);

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(result));
        Assert.Equal("lost_race", ReasonOf(result));
        Assert.True(RetriableOf(result));
    }

    /// <summary>A completed confirm that left the order pending is a bug and throws.</summary>
    [Fact]
    public void ForConfirm_WhenCompletedButStillPending_ShouldThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OrderResults.ForConfirm(
                new OrderActionResult(OrderActionOutcome.Completed, AnOrder(OrderStatus.Pending)),
                Path));
    }

    // -- Cancel -----------------------------------------------------------

    [Fact]
    public void ForCancel_WhenCancelled_ShouldBe200()
    {
        var result = OrderResults.ForCancel(
            new OrderActionResult(OrderActionOutcome.Completed, AnOrder(OrderStatus.Cancelled)),
            Path);

        var ok = Assert.IsType<Ok<OrderResponse>>(result);
        Assert.Equal("cancelled", ok.Value!.Status);
    }

    [Fact]
    public void ForCancel_WhenOrderMissing_ShouldBe404()
    {
        var result = OrderResults.ForCancel(
            new OrderActionResult(OrderActionOutcome.OrderNotFound), Path);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
    }

    [Fact]
    public void ForCancel_WhenAlreadyEnded_ShouldBe409()
    {
        var result = OrderResults.ForCancel(
            new OrderActionResult(OrderActionOutcome.NotPending, AnOrder(OrderStatus.Confirmed)),
            Path);

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(result));
        Assert.Equal("order_not_pending", ReasonOf(result));
    }

    // -- Reading ----------------------------------------------------------

    /// <summary>A pending order whose holds lapsed still reads pending; only Inventory can say otherwise.</summary>
    [Fact]
    public void ForRead_ShouldReturnTheStoredStatusWithoutDerivingExpiry()
    {
        var order = AnOrder(OrderStatus.Pending);
        order.HoldsExpireAt = PlacedAt.AddMinutes(-60);

        var ok = Assert.IsType<Ok<OrderResponse>>(OrderResults.ForRead(order));

        Assert.Equal("pending", ok.Value!.Status);
        Assert.Equal(order.HoldsExpireAt, ok.Value.HoldsExpireAt);
    }
}
