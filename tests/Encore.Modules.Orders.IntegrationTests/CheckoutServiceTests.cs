using Encore.Modules.Catalog.Contracts;
using Encore.Modules.Inventory.Contracts;
using Encore.Modules.Orders.Data;
using Encore.Modules.Orders.Models;
using Encore.Modules.Payments.Contracts;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Orders.IntegrationTests;

/// <summary>
/// Checkout, confirm and cancel against real Postgres, with Catalog, Inventory and Payments
/// faked at their contracts. Every test uses fresh ids, since rows accumulate per class.
/// </summary>
public sealed class CheckoutServiceTests : IAsyncLifetime
{
    private static readonly DateTime OnSale = new(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Starts = new(2026, 6, 1, 19, 30, 0, DateTimeKind.Utc);
    private const decimal UnitPrice = 25m;
    private const string Currency = "GBP";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("encore")
        .WithUsername("encore")
        .WithPassword("encore")
        .Build();

    private DbContextOptions<OrdersDbContext> _options = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        _options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseOrdersNpgsql(_postgres.GetConnectionString())
            .Options;

        await using var context = new OrdersDbContext(_options);
        await context.Database.MigrateAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    // -- Checkout ---------------------------------------------------------

    [Fact]
    public async Task Checkout_WhenEverySeatIsHeld_ShouldOpenAPendingOrder()
    {
        var clientId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var seats = new FakeSeatReservations();
        seats.HoldsExpiringAt[first] = Now.AddMinutes(5);
        seats.HoldsExpiringAt[second] = Now.AddMinutes(4);

        await using var context = new OrdersDbContext(_options);
        var result = await ServiceFor(context, seats)
            .CheckoutAsync(clientId, eventId, [first, second]);

        Assert.Equal(CheckoutOutcome.Created, result.Outcome);

        var order = result.Order!;
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal(UnitPrice * 2, order.Total);
        Assert.Equal(Currency, order.Currency);
        Assert.Equal(Now, order.PlacedAt);
        Assert.Equal(2, order.Lines.Count);

        // The earliest of the two, because the order needs both seats.
        Assert.Equal(Now.AddMinutes(4), order.HoldsExpireAt);

        await using var reader = new OrdersDbContext(_options);
        var stored = await reader.Orders.Include(o => o.Lines).SingleAsync(o => o.Id == order.Id);
        Assert.Equal(2, stored.Lines.Count);
        Assert.All(stored.Lines, line => Assert.Equal(UnitPrice, line.UnitPrice));
    }

    /// <summary>A partial checkout writes nothing and releases nothing.</summary>
    [Fact]
    public async Task Checkout_WhenASeatIsRefused_ShouldWriteNothingAndReleaseNothing()
    {
        var clientId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();

        var seats = new FakeSeatReservations();
        seats.HoldsExpiringAt[mine] = Now.AddMinutes(5);
        seats.HoldRefusals[theirs] = HoldSeatStatus.AlreadyHeld;

        await using var context = new OrdersDbContext(_options);
        var result = await ServiceFor(context, seats).CheckoutAsync(clientId, eventId, [mine, theirs]);

        Assert.Equal(CheckoutOutcome.SeatsUnavailable, result.Outcome);
        Assert.Null(result.Order);

        // Nothing given back: the seat that was held is still the client's.
        Assert.Empty(seats.Releases);

        await using var reader = new OrdersDbContext(_options);
        Assert.False(await reader.Orders.AnyAsync(order => order.ClientId == clientId));
    }

    /// <summary>Every seat is attempted, so the client learns about all refusals at once.</summary>
    [Fact]
    public async Task Checkout_WhenSeatsAreRefused_ShouldReportEveryOne()
    {
        var clientId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var gone = Guid.NewGuid();
        var sold = Guid.NewGuid();
        var free = Guid.NewGuid();

        var seats = new FakeSeatReservations();
        seats.HoldRefusals[gone] = HoldSeatStatus.AlreadyHeld;
        seats.HoldRefusals[sold] = HoldSeatStatus.AlreadySold;
        seats.HoldsExpiringAt[free] = Now.AddMinutes(5);

        await using var context = new OrdersDbContext(_options);
        var result = await ServiceFor(context, seats).CheckoutAsync(clientId, eventId, [gone, sold, free]);

        Assert.Equal(CheckoutOutcome.SeatsUnavailable, result.Outcome);
        Assert.Equal(3, Assert.Single(seats.Holds).SeatIds.Count);

        Assert.Collection(
            result.Refusals!,
            refusal => Assert.Equal(HoldSeatStatus.AlreadyHeld, refusal.Status),
            refusal => Assert.Equal(HoldSeatStatus.AlreadySold, refusal.Status));
    }

    [Fact]
    public async Task Checkout_WithNoSeats_ShouldBeRefused()
    {
        await using var context = new OrdersDbContext(_options);
        var seats = new FakeSeatReservations();

        var result = await ServiceFor(context, seats)
            .CheckoutAsync(Guid.NewGuid(), Guid.NewGuid(), []);

        Assert.Equal(CheckoutOutcome.NoSeats, result.Outcome);
        Assert.Empty(seats.Holds);
    }

    /// <summary>A repeated seat id is refused, not collapsed.</summary>
    [Fact]
    public async Task Checkout_WithADuplicateSeat_ShouldBeRefusedWithoutHolding()
    {
        var seatId = Guid.NewGuid();

        await using var context = new OrdersDbContext(_options);
        var seats = new FakeSeatReservations();

        var result = await ServiceFor(context, seats)
            .CheckoutAsync(Guid.NewGuid(), Guid.NewGuid(), [seatId, seatId]);

        Assert.Equal(CheckoutOutcome.DuplicateSeat, result.Outcome);
        Assert.Empty(seats.Holds);
    }

    /// <summary>Duplicates are judged before the cap.</summary>
    [Fact]
    public async Task Checkout_WithDuplicatesBeyondTheCap_ShouldReportTheDuplicateNotTheCap()
    {
        var seatId = Guid.NewGuid();
        var tooMany = Enumerable
            .Repeat(seatId, SeatReservationLimits.MaxHoldsPerClientPerEvent + 1)
            .ToArray();

        await using var context = new OrdersDbContext(_options);

        var result = await ServiceFor(context, new FakeSeatReservations())
            .CheckoutAsync(Guid.NewGuid(), Guid.NewGuid(), tooMany);

        Assert.Equal(CheckoutOutcome.DuplicateSeat, result.Outcome);
    }

    /// <summary>The cap is read from the published contract.</summary>
    [Fact]
    public async Task Checkout_BeyondTheCap_ShouldBeRefusedWithoutHolding()
    {
        var tooMany = Enumerable
            .Range(0, SeatReservationLimits.MaxHoldsPerClientPerEvent + 1)
            .Select(_ => Guid.NewGuid())
            .ToArray();

        await using var context = new OrdersDbContext(_options);
        var seats = new FakeSeatReservations();

        var result = await ServiceFor(context, seats)
            .CheckoutAsync(Guid.NewGuid(), Guid.NewGuid(), tooMany);

        Assert.Equal(CheckoutOutcome.TooManySeats, result.Outcome);
        Assert.Empty(seats.Holds);
    }

    [Fact]
    public async Task Checkout_WhenTheEventIsUnknown_ShouldBeRefusedWithoutHolding()
    {
        await using var context = new OrdersDbContext(_options);
        var seats = new FakeSeatReservations();
        var pricing = new FakeEventPricing { Response = EventPricingResponse.EventNotFound };

        var result = await ServiceFor(context, seats, pricing)
            .CheckoutAsync(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()]);

        Assert.Equal(CheckoutOutcome.EventNotFound, result.Outcome);
        Assert.Empty(seats.Holds);
    }

    /// <summary>Orders enforces the on-sale time Catalog states.</summary>
    [Fact]
    public async Task Checkout_BeforeTheEventGoesOnSale_ShouldBeRefusedWithoutHolding()
    {
        await using var context = new OrdersDbContext(_options);
        var seats = new FakeSeatReservations();
        var pricing = new FakeEventPricing
        {
            Response = EventPricingResponse.Priced(UnitPrice, Currency, Now.AddHours(1), Starts)
        };

        var result = await ServiceFor(context, seats, pricing)
            .CheckoutAsync(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()]);

        Assert.Equal(CheckoutOutcome.NotOnSale, result.Outcome);
        Assert.Empty(seats.Holds);
    }

    /// <summary>Sales stay open after the show starts: the gate is a lower bound only.</summary>
    [Fact]
    public async Task Checkout_AfterTheShowHasStarted_ShouldStillBeAllowed()
    {
        var seatId = Guid.NewGuid();
        var seats = new FakeSeatReservations();
        seats.HoldsExpiringAt[seatId] = Now.AddMinutes(5);

        var pricing = new FakeEventPricing
        {
            Response = EventPricingResponse.Priced(UnitPrice, Currency, OnSale, Now.AddHours(-2))
        };

        await using var context = new OrdersDbContext(_options);
        var result = await ServiceFor(context, seats, pricing)
            .CheckoutAsync(Guid.NewGuid(), Guid.NewGuid(), [seatId]);

        Assert.Equal(CheckoutOutcome.Created, result.Outcome);
    }

    /// <summary>No on-sale time means on sale now.</summary>
    [Fact]
    public async Task Checkout_WhenTheEventHasNoSaleWindow_ShouldBeAllowed()
    {
        var seatId = Guid.NewGuid();
        var seats = new FakeSeatReservations();
        seats.HoldsExpiringAt[seatId] = Now.AddMinutes(5);

        var pricing = new FakeEventPricing
        {
            Response = EventPricingResponse.Priced(UnitPrice, Currency, null, Starts)
        };

        await using var context = new OrdersDbContext(_options);
        var result = await ServiceFor(context, seats, pricing)
            .CheckoutAsync(Guid.NewGuid(), Guid.NewGuid(), [seatId]);

        Assert.Equal(CheckoutOutcome.Created, result.Outcome);
    }

    [Fact]
    public async Task Checkout_WhenTheClientAlreadyHasAnOpenCheckout_ShouldBeRefused()
    {
        var clientId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var seats = new FakeSeatReservations();
        seats.HoldsExpiringAt[first] = Now.AddMinutes(5);
        seats.HoldsExpiringAt[second] = Now.AddMinutes(5);

        await using var context = new OrdersDbContext(_options);
        var service = ServiceFor(context, seats);

        Assert.Equal(
            CheckoutOutcome.Created,
            (await service.CheckoutAsync(clientId, eventId, [first])).Outcome);

        await using var another = new OrdersDbContext(_options);
        var result = await ServiceFor(another, seats).CheckoutAsync(clientId, eventId, [second]);

        Assert.Equal(CheckoutOutcome.CheckoutAlreadyOpen, result.Outcome);
    }

    /// <summary>The one-open-checkout index is scoped to the client and the event together.</summary>
    [Fact]
    public async Task Checkout_WhenTheOpenCheckoutIsForAnotherEvent_ShouldBeAllowed()
    {
        var clientId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var seats = new FakeSeatReservations();
        seats.HoldsExpiringAt[first] = Now.AddMinutes(5);
        seats.HoldsExpiringAt[second] = Now.AddMinutes(5);

        await using var context = new OrdersDbContext(_options);
        var service = ServiceFor(context, seats);

        await service.CheckoutAsync(clientId, Guid.NewGuid(), [first]);

        await using var another = new OrdersDbContext(_options);
        var result = await ServiceFor(another, seats).CheckoutAsync(clientId, Guid.NewGuid(), [second]);

        Assert.Equal(CheckoutOutcome.Created, result.Outcome);
    }

    // -- Confirm ----------------------------------------------------------

    /// <summary>
    /// Orders never judges hold expiry itself: with the clock far past <c>HoldsExpireAt</c> and
    /// Inventory reporting a sale, the order confirms.
    /// </summary>
    [Fact]
    public async Task Confirm_WhenHoldsLapsed_ShouldAskInventoryRatherThanItsOwnClock()
    {
        var clientId = Guid.NewGuid();
        var order = await AnOpenOrderAsync(clientId, seatCount: 1);

        var seats = new FakeSeatReservations { DefaultSell = SellSeatStatus.Sold };

        await using var context = new OrdersDbContext(_options);

        // An hour after every hold on this order lapsed.
        var result = await ServiceFor(context, seats, at: Now.AddHours(1))
            .ConfirmAsync(clientId, order.Id);

        Assert.Equal(OrderActionOutcome.Completed, result.Outcome);
        Assert.Equal(OrderStatus.Confirmed, result.Order!.Status);
    }

    [Fact]
    public async Task Confirm_WhenEverySeatSells_ShouldConfirmAndCloseTheOrder()
    {
        var clientId = Guid.NewGuid();
        var order = await AnOpenOrderAsync(clientId, seatCount: 2);

        await using var context = new OrdersDbContext(_options);
        var result = await ServiceFor(context, new FakeSeatReservations())
            .ConfirmAsync(clientId, order.Id);

        Assert.Equal(OrderStatus.Confirmed, result.Order!.Status);

        await using var reader = new OrdersDbContext(_options);
        var stored = await reader.Orders.SingleAsync(o => o.Id == order.Id);

        Assert.Equal(OrderStatus.Confirmed, stored.Status);
        Assert.Equal(Now, stored.ClosedAt);

        // Cleared because it describes an order that can still be completed.
        Assert.Null(stored.HoldsExpireAt);
    }

    /// <summary>Nothing sold and every refusal an expiry ends the order Expired.</summary>
    [Fact]
    public async Task Confirm_WhenEveryHoldHasLapsed_ShouldExpireTheOrder()
    {
        var clientId = Guid.NewGuid();
        var order = await AnOpenOrderAsync(clientId, seatCount: 2);

        var seats = new FakeSeatReservations { DefaultSell = SellSeatStatus.HoldExpired };

        await using var context = new OrdersDbContext(_options);
        var result = await ServiceFor(context, seats).ConfirmAsync(clientId, order.Id);

        Assert.Equal(OrderActionOutcome.Completed, result.Outcome);
        Assert.Equal(OrderStatus.Expired, result.Order!.Status);
    }

    /// <summary>
    /// One hold lapsed, one live: the seats are sold together, so nothing sells.
    /// </summary>
    [Fact]
    public async Task Confirm_WhenOneHoldHasLapsed_ShouldSellTheSeatsTogetherAndExpireTheOrder()
    {
        var clientId = Guid.NewGuid();
        var order = await AnOpenOrderAsync(clientId, seatCount: 2);
        var lost = order.Lines[1].SeatId;

        var seats = new FakeSeatReservations();
        seats.SellRefusals[lost] = SellSeatStatus.HoldExpired;

        await using var context = new OrdersDbContext(_options);
        var result = await ServiceFor(context, seats).ConfirmAsync(clientId, order.Id);

        var sale = Assert.Single(seats.Sells);
        Assert.Equal(order.Lines.Select(line => line.SeatId).Order(), sale.SeatIds.Order());

        // Every refusal was an expiry, so this is the ordinary ending; the live seat never sold.
        Assert.Equal(OrderStatus.Expired, result.Order!.Status);
    }

    /// <summary>A refusal other than expiry ends the order Failed, even with nothing sold.</summary>
    [Fact]
    public async Task Confirm_WhenARefusalIsNotExpiry_ShouldFailRatherThanExpire()
    {
        var clientId = Guid.NewGuid();
        var order = await AnOpenOrderAsync(clientId, seatCount: 1);

        var seats = new FakeSeatReservations { DefaultSell = SellSeatStatus.AlreadySold };

        await using var context = new OrdersDbContext(_options);
        var result = await ServiceFor(context, seats).ConfirmAsync(clientId, order.Id);

        Assert.Equal(OrderStatus.Failed, result.Order!.Status);
    }

    /// <summary>A retried confirm is not told its completed order failed.</summary>
    [Fact]
    public async Task Confirm_WhenAlreadyConfirmed_ShouldSucceedWithoutAskingAgain()
    {
        var clientId = Guid.NewGuid();
        var order = await AnOpenOrderAsync(clientId, seatCount: 1);

        await using (var first = new OrdersDbContext(_options))
        {
            await ServiceFor(first, new FakeSeatReservations()).ConfirmAsync(clientId, order.Id);
        }

        var seats = new FakeSeatReservations();

        await using var context = new OrdersDbContext(_options);
        var result = await ServiceFor(context, seats).ConfirmAsync(clientId, order.Id);

        Assert.Equal(OrderActionOutcome.Completed, result.Outcome);
        Assert.Equal(OrderStatus.Confirmed, result.Order!.Status);
        Assert.Empty(seats.Sells);
    }

    [Fact]
    public async Task Confirm_WhenTheOrderIsSomebodyElses_ShouldBeIndistinguishableFromMissing()
    {
        var order = await AnOpenOrderAsync(Guid.NewGuid(), seatCount: 1);

        await using var context = new OrdersDbContext(_options);
        var result = await ServiceFor(context, new FakeSeatReservations())
            .ConfirmAsync(Guid.NewGuid(), order.Id);

        Assert.Equal(OrderActionOutcome.OrderNotFound, result.Outcome);
    }

    // -- Confirm: the money -----------------------------------------------

    /// <summary>No seat is sold until the money is secured.</summary>
    [Theory]
    [InlineData(AuthorizePaymentStatus.Declined, OrderActionOutcome.PaymentDeclined)]
    [InlineData(AuthorizePaymentStatus.TimedOut, OrderActionOutcome.PaymentTimedOut)]
    [InlineData(AuthorizePaymentStatus.ConcurrentAttemptInFlight, OrderActionOutcome.LostRace)]
    public async Task Confirm_WhenTheMoneyCannotBeSecured_ShouldSellNothingAndLeaveTheOrderPending(
        AuthorizePaymentStatus refusal,
        OrderActionOutcome expected)
    {
        var clientId = Guid.NewGuid();
        var order = await AnOpenOrderAsync(clientId, seatCount: 2);

        var seats = new FakeSeatReservations();
        var payments = new FakeOrderPayments { AuthorizeWith = refusal };

        await using var context = new OrdersDbContext(_options);
        var result = await ServiceFor(context, seats, payments: payments)
            .ConfirmAsync(clientId, order.Id);

        Assert.Equal(expected, result.Outcome);
        Assert.Empty(seats.Sells);
        Assert.Empty(payments.Captures);

        // The holds are still live, so a decline does not end the order.
        await using var reader = new OrdersDbContext(_options);
        var stored = await reader.Orders.SingleAsync(candidate => candidate.Id == order.Id);
        Assert.Equal(OrderStatus.Pending, stored.Status);
        Assert.NotNull(stored.HoldsExpireAt);
        Assert.Null(stored.ClosedAt);
    }

    [Fact]
    public async Task Confirm_ShouldAuthorizeExactlyWhatTheOrderSaysIsOwed()
    {
        var clientId = Guid.NewGuid();
        var order = await AnOpenOrderAsync(clientId, seatCount: 2);

        var payments = new FakeOrderPayments();

        await using var context = new OrdersDbContext(_options);
        await ServiceFor(context, new FakeSeatReservations(), payments: payments)
            .ConfirmAsync(clientId, order.Id);

        var authorized = Assert.Single(payments.Authorizations);
        Assert.Equal(order.Id, authorized.OrderId);
        Assert.Equal(clientId, authorized.ClientId);
        Assert.Equal(UnitPrice * 2, authorized.Amount);
        Assert.Equal(Currency, authorized.Currency);
    }

    /// <summary>A sale that does not complete voids the authorisation, so nothing is charged.</summary>
    [Theory]
    [InlineData(SellSeatStatus.HoldExpired, OrderStatus.Expired)]
    [InlineData(SellSeatStatus.AlreadySold, OrderStatus.Failed)]
    public async Task Confirm_WhenTheSaleDoesNotComplete_ShouldReleaseTheMoneyAndTakeNone(
        SellSeatStatus refusal,
        OrderStatus expected)
    {
        var clientId = Guid.NewGuid();
        var order = await AnOpenOrderAsync(clientId, seatCount: 2);

        var seats = new FakeSeatReservations { DefaultSell = refusal };
        var payments = new FakeOrderPayments();

        await using var context = new OrdersDbContext(_options);
        var result = await ServiceFor(context, seats, payments: payments)
            .ConfirmAsync(clientId, order.Id);

        Assert.Equal(expected, result.Order!.Status);
        Assert.Single(payments.Voids);
        Assert.Empty(payments.Captures);
    }

    /// <summary>A non-expiry refusal: nothing sold, order Failed, nothing charged.</summary>
    [Fact]
    public async Task Confirm_WhenOneSeatIsNoLongerTheirs_ShouldFailAndChargeNothing()
    {
        var clientId = Guid.NewGuid();
        var order = await AnOpenOrderAsync(clientId, seatCount: 2);

        var seats = new FakeSeatReservations();
        seats.SellRefusals[order.Lines[1].SeatId] = SellSeatStatus.NotTheHolder;
        var payments = new FakeOrderPayments();

        await using var context = new OrdersDbContext(_options);
        var result = await ServiceFor(context, seats, payments: payments)
            .ConfirmAsync(clientId, order.Id);

        Assert.Equal(OrderStatus.Failed, result.Order!.Status);
        Assert.Single(payments.Voids);
        Assert.Empty(payments.Captures);
    }

    /// <summary>Seats sold but the capture unanswered: AwaitingCapture, not closed.</summary>
    [Fact]
    public async Task Confirm_WhenTheCaptureGoesUnanswered_ShouldAwaitCaptureRatherThanFail()
    {
        var clientId = Guid.NewGuid();
        var order = await AnOpenOrderAsync(clientId, seatCount: 1);

        var payments = new FakeOrderPayments { CaptureWith = CapturePaymentStatus.TimedOut };

        await using var context = new OrdersDbContext(_options);
        var result = await ServiceFor(context, new FakeSeatReservations(), payments: payments)
            .ConfirmAsync(clientId, order.Id);

        Assert.Equal(OrderActionOutcome.Completed, result.Outcome);
        Assert.Equal(OrderStatus.AwaitingCapture, result.Order!.Status);
        Assert.Null(result.Order.ClosedAt);
        Assert.Null(result.Order.HoldsExpireAt);
        Assert.Empty(payments.Voids);
    }

    /// <summary>The next confirm retries only the capture.</summary>
    [Fact]
    public async Task Confirm_WhenRetriedAfterAnUnansweredCapture_ShouldCaptureAndConfirm()
    {
        var clientId = Guid.NewGuid();
        var order = await AnOpenOrderAsync(clientId, seatCount: 1);

        var payments = new FakeOrderPayments { CaptureWith = CapturePaymentStatus.TimedOut };

        await using (var first = new OrdersDbContext(_options))
        {
            await ServiceFor(first, new FakeSeatReservations(), payments: payments)
                .ConfirmAsync(clientId, order.Id);
        }

        payments.CaptureWith = CapturePaymentStatus.Captured;
        var seats = new FakeSeatReservations();

        await using var context = new OrdersDbContext(_options);
        var result = await ServiceFor(context, seats, payments: payments)
            .ConfirmAsync(clientId, order.Id);

        Assert.Equal(OrderActionOutcome.Completed, result.Outcome);
        Assert.Equal(OrderStatus.Confirmed, result.Order!.Status);
        Assert.Equal(Now, result.Order.ClosedAt);

        Assert.Empty(seats.Sells);
        Assert.Single(payments.Authorizations);
        Assert.Equal(2, payments.Captures.Count);
    }

    /// <summary>Seats sold with no authorisation behind them ends Failed.</summary>
    [Fact]
    public async Task Confirm_WhenTheCaptureFindsNothingHeld_ShouldFailTheOrder()
    {
        var clientId = Guid.NewGuid();
        var order = await AnOpenOrderAsync(clientId, seatCount: 1);

        var payments = new FakeOrderPayments { CaptureWith = CapturePaymentStatus.NoAuthorization };

        await using var context = new OrdersDbContext(_options);
        var result = await ServiceFor(context, new FakeSeatReservations(), payments: payments)
            .ConfirmAsync(clientId, order.Id);

        Assert.Equal(OrderStatus.Failed, result.Order!.Status);
    }

    /// <summary>An earlier confirm that captured but did not record the order is healed by carrying on.</summary>
    [Fact]
    public async Task Confirm_WhenTheMoneyWasAlreadyTaken_ShouldCarryOnAndConfirm()
    {
        var clientId = Guid.NewGuid();
        var order = await AnOpenOrderAsync(clientId, seatCount: 1);

        var payments = new FakeOrderPayments
        {
            AuthorizeWith = AuthorizePaymentStatus.AlreadyCaptured
        };

        await using var context = new OrdersDbContext(_options);
        var result = await ServiceFor(context, new FakeSeatReservations(), payments: payments)
            .ConfirmAsync(clientId, order.Id);

        Assert.Equal(OrderActionOutcome.Completed, result.Outcome);
        Assert.Equal(OrderStatus.Confirmed, result.Order!.Status);
    }

    // -- Cancel -----------------------------------------------------------

    /// <summary>Cancelling ends the order and releases the seats and the money.</summary>
    [Fact]
    public async Task Cancel_ShouldEndTheOrderAndHandBackTheSeatsAndTheMoney()
    {
        var clientId = Guid.NewGuid();
        var order = await AnOpenOrderAsync(clientId, seatCount: 2);

        var seats = new FakeSeatReservations();
        var payments = new FakeOrderPayments();

        await using var context = new OrdersDbContext(_options);
        var result = await ServiceFor(context, seats, payments: payments)
            .CancelAsync(clientId, order.Id);

        Assert.Equal(OrderActionOutcome.Completed, result.Outcome);
        Assert.Equal(OrderStatus.Cancelled, result.Order!.Status);
        Assert.Equal(Now, result.Order.ClosedAt);
        Assert.Null(result.Order.HoldsExpireAt);

        var release = Assert.Single(seats.Releases);
        Assert.Equal(order.Lines.Select(line => line.SeatId).Order(), release.SeatIds.Order());
        Assert.Equal(clientId, release.ClientId);
        Assert.Single(payments.Voids);
    }

    /// <summary>
    /// A confirm of this order has sold the seats: the cancel touches neither the money nor the order.
    /// </summary>
    [Fact]
    public async Task Cancel_WhenAConfirmHasSoldTheSeats_ShouldLeaveTheMoneyAndTheOrderAlone()
    {
        var clientId = Guid.NewGuid();
        var order = await AnOpenOrderAsync(clientId, seatCount: 2);

        var seats = new FakeSeatReservations { DefaultRelease = ReleaseSeatStatus.SoldToYou };
        var payments = new FakeOrderPayments();

        await using var context = new OrdersDbContext(_options);
        var result = await ServiceFor(context, seats, payments: payments)
            .CancelAsync(clientId, order.Id);

        Assert.Equal(OrderActionOutcome.LostRace, result.Outcome);
        Assert.Empty(payments.Voids);

        await using var reader = new OrdersDbContext(_options);
        var stored = await reader.Orders.SingleAsync(candidate => candidate.Id == order.Id);
        Assert.Equal(OrderStatus.Pending, stored.Status);
    }

    /// <summary>A seat sold to someone else does not stop the cancellation; the money goes back.</summary>
    [Fact]
    public async Task Cancel_WhenASeatWasSoldToSomebodyElse_ShouldStillCancelAndVoid()
    {
        var clientId = Guid.NewGuid();
        var order = await AnOpenOrderAsync(clientId, seatCount: 2);

        var seats = new FakeSeatReservations();
        seats.ReleaseAnswers[order.Lines[0].SeatId] = ReleaseSeatStatus.AlreadySold;
        var payments = new FakeOrderPayments();

        await using var context = new OrdersDbContext(_options);
        var result = await ServiceFor(context, seats, payments: payments)
            .CancelAsync(clientId, order.Id);

        Assert.Equal(OrderActionOutcome.Completed, result.Outcome);
        Assert.Equal(OrderStatus.Cancelled, result.Order!.Status);
        Assert.Single(payments.Voids);
    }

    /// <summary>
    /// Defensive: if Payments says the money was taken, no cancellation is written over it.
    /// </summary>
    [Fact]
    public async Task Cancel_WhenTheMoneyHasAlreadyBeenTaken_ShouldSayLostRace()
    {
        var clientId = Guid.NewGuid();
        var order = await AnOpenOrderAsync(clientId, seatCount: 1);

        var seats = new FakeSeatReservations();
        var payments = new FakeOrderPayments { VoidWith = VoidPaymentStatus.AlreadyCaptured };

        await using var context = new OrdersDbContext(_options);
        var result = await ServiceFor(context, seats, payments: payments)
            .CancelAsync(clientId, order.Id);

        Assert.Equal(OrderActionOutcome.LostRace, result.Outcome);

        await using var reader = new OrdersDbContext(_options);
        var stored = await reader.Orders.SingleAsync(candidate => candidate.Id == order.Id);
        Assert.Equal(OrderStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task Cancel_WhenAlreadyCancelled_ShouldSucceed()
    {
        var clientId = Guid.NewGuid();
        var order = await AnOpenOrderAsync(clientId, seatCount: 1);

        await using (var first = new OrdersDbContext(_options))
        {
            await ServiceFor(first, new FakeSeatReservations()).CancelAsync(clientId, order.Id);
        }

        await using var context = new OrdersDbContext(_options);
        var result = await ServiceFor(context, new FakeSeatReservations()).CancelAsync(clientId, order.Id);

        Assert.Equal(OrderActionOutcome.Completed, result.Outcome);
        Assert.Equal(OrderStatus.Cancelled, result.Order!.Status);
    }

    [Fact]
    public async Task Cancel_WhenTheOrderIsConfirmed_ShouldBeRefused()
    {
        var clientId = Guid.NewGuid();
        var order = await AnOpenOrderAsync(clientId, seatCount: 1);

        await using (var first = new OrdersDbContext(_options))
        {
            await ServiceFor(first, new FakeSeatReservations()).ConfirmAsync(clientId, order.Id);
        }

        await using var context = new OrdersDbContext(_options);
        var result = await ServiceFor(context, new FakeSeatReservations()).CancelAsync(clientId, order.Id);

        Assert.Equal(OrderActionOutcome.NotPending, result.Outcome);
        Assert.Equal(OrderStatus.Confirmed, result.Order!.Status);
    }

    /// <summary>Ending an order frees the client to open another for the same event.</summary>
    [Fact]
    public async Task Checkout_AfterCancelling_ShouldBeAllowedAgain()
    {
        var clientId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var seats = new FakeSeatReservations();
        seats.HoldsExpiringAt[first] = Now.AddMinutes(5);
        seats.HoldsExpiringAt[second] = Now.AddMinutes(5);

        Guid orderId;
        await using (var context = new OrdersDbContext(_options))
        {
            var opened = await ServiceFor(context, seats).CheckoutAsync(clientId, eventId, [first]);
            orderId = opened.Order!.Id;
        }

        await using (var context = new OrdersDbContext(_options))
        {
            await ServiceFor(context, seats).CancelAsync(clientId, orderId);
        }

        await using var another = new OrdersDbContext(_options);
        var result = await ServiceFor(another, seats).CheckoutAsync(clientId, eventId, [second]);

        Assert.Equal(CheckoutOutcome.Created, result.Outcome);
    }

    // -- Confirm and cancel together --------------------------------------

    /// <summary>
    /// A cancel runs between the confirm's sale and its capture. It sees its own confirm sold the
    /// seats and leaves the money alone.
    /// </summary>
    [Fact]
    public async Task Cancel_BetweenAConfirmsSaleAndItsCapture_ShouldLeaveTheSaleToBePaidFor()
    {
        var clientId = Guid.NewGuid();
        var order = await AnOpenOrderAsync(clientId, seatCount: 2);

        var seats = new StatefulSeatReservations(order);
        var payments = new StatefulOrderPayments();

        OrderActionResult? cancel = null;
        payments.BeforeCapture = async () =>
        {
            await using var other = new OrdersDbContext(_options);
            cancel = await ServiceFor(other, seats, payments: payments).CancelAsync(clientId, order.Id);
        };

        await using var context = new OrdersDbContext(_options);
        var confirm = await ServiceFor(context, seats, payments: payments).ConfirmAsync(clientId, order.Id);

        // Sold seats and taken money go together.
        Assert.All(seats.Seats.Values, seat => Assert.Equal(SeatState.Sold, seat));
        Assert.Equal(MoneyState.Captured, payments.Money);

        Assert.Equal(OrderActionOutcome.LostRace, cancel!.Outcome);
        Assert.Equal(OrderActionOutcome.Completed, confirm.Outcome);

        await using var reader = new OrdersDbContext(_options);
        var stored = await reader.Orders.SingleAsync(candidate => candidate.Id == order.Id);
        Assert.Equal(OrderStatus.Confirmed, stored.Status);
    }

    /// <summary>
    /// A cancel between the authorisation and the sale: the seats go back first, the sale finds
    /// nothing, and both sides release the money.
    /// </summary>
    [Fact]
    public async Task Cancel_BetweenAConfirmsAuthorisationAndItsSale_ShouldLeaveNothingSoldAndNothingTaken()
    {
        var clientId = Guid.NewGuid();
        var order = await AnOpenOrderAsync(clientId, seatCount: 2);

        var seats = new StatefulSeatReservations(order);
        var payments = new StatefulOrderPayments();

        OrderActionResult? cancel = null;
        seats.BeforeSell = async () =>
        {
            await using var other = new OrdersDbContext(_options);
            cancel = await ServiceFor(other, seats, payments: payments).CancelAsync(clientId, order.Id);
        };

        await using var context = new OrdersDbContext(_options);
        var confirm = await ServiceFor(context, seats, payments: payments).ConfirmAsync(clientId, order.Id);

        Assert.All(seats.Seats.Values, seat => Assert.Equal(SeatState.Available, seat));
        Assert.Equal(MoneyState.Voided, payments.Money);

        Assert.Equal(OrderActionOutcome.Completed, cancel!.Outcome);
        Assert.Equal(OrderActionOutcome.LostRace, confirm.Outcome);

        await using var reader = new OrdersDbContext(_options);
        var stored = await reader.Orders.SingleAsync(candidate => candidate.Id == order.Id);
        Assert.Equal(OrderStatus.Cancelled, stored.Status);
    }

    // -- Helpers ----------------------------------------------------------

    private CheckoutService ServiceFor(
        OrdersDbContext context,
        ISeatReservations seats,
        FakeEventPricing? pricing = null,
        DateTime? at = null,
        IOrderPayments? payments = null) =>
        new(
            context,
            pricing ?? new FakeEventPricing(),
            seats,
            payments ?? new FakeOrderPayments(),
            new FixedTimeProvider(at ?? Now));

    /// <summary>A committed, detached pending order with the given number of seats.</summary>
    private async Task<Order> AnOpenOrderAsync(Guid clientId, int seatCount)
    {
        var seats = new FakeSeatReservations();
        var seatIds = Enumerable.Range(0, seatCount).Select(_ => Guid.NewGuid()).ToArray();

        foreach (var seatId in seatIds)
        {
            seats.HoldsExpiringAt[seatId] = Now.AddMinutes(5);
        }

        await using var context = new OrdersDbContext(_options);
        var result = await ServiceFor(context, seats).CheckoutAsync(clientId, Guid.NewGuid(), seatIds);

        return result.Order!;
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    /// <summary>Catalog faked at its contract: priced and on sale unless a test says otherwise.</summary>
    private sealed class FakeEventPricing : IEventPricing
    {
        public EventPricingResponse Response { get; set; } =
            EventPricingResponse.Priced(UnitPrice, Currency, OnSale, Starts);

        public Task<EventPricingResponse> GetAsync(
            EventPricingRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Response);
    }

    /// <summary>Inventory faked at its contract. One refusing seat means none sell.</summary>
    private sealed class FakeSeatReservations : ISeatReservations
    {
        public Dictionary<Guid, DateTime> HoldsExpiringAt { get; } = [];

        public Dictionary<Guid, HoldSeatStatus> HoldRefusals { get; } = [];

        public Dictionary<Guid, SellSeatStatus> SellRefusals { get; } = [];

        public SellSeatStatus DefaultSell { get; set; } = SellSeatStatus.Sold;

        public Dictionary<Guid, ReleaseSeatStatus> ReleaseAnswers { get; } = [];

        public ReleaseSeatStatus DefaultRelease { get; set; } = ReleaseSeatStatus.Released;

        public List<HoldSeatsRequest> Holds { get; } = [];

        public List<SellSeatsRequest> Sells { get; } = [];

        public List<ReleaseSeatsRequest> Releases { get; } = [];

        public Task<HoldSeatsResponse> HoldAsync(
            HoldSeatsRequest request,
            CancellationToken cancellationToken = default)
        {
            Holds.Add(request);

            return Task.FromResult(new HoldSeatsResponse([.. request.SeatIds.Select(HoldOne)]));
        }

        public Task<ReleaseSeatsResponse> ReleaseAsync(
            ReleaseSeatsRequest request,
            CancellationToken cancellationToken = default)
        {
            Releases.Add(request);

            return Task.FromResult(new ReleaseSeatsResponse(
                [.. request.SeatIds.Select(seatId => new ReleaseSeatResponse(
                    seatId,
                    ReleaseAnswers.TryGetValue(seatId, out var answer) ? answer : DefaultRelease))]));
        }

        public Task<SellSeatsResponse> SellAsync(
            SellSeatsRequest request,
            CancellationToken cancellationToken = default)
        {
            Sells.Add(request);

            var refusals = request.SeatIds
                .Select(seatId => new SellSeatResponse(
                    seatId,
                    SellRefusals.TryGetValue(seatId, out var refusal) ? refusal : DefaultSell))
                .Where(answer => answer.Status is not SellSeatStatus.Sold)
                .ToList();

            return Task.FromResult(new SellSeatsResponse(refusals));
        }

        private HoldSeatResponse HoldOne(Guid seatId)
        {
            if (HoldRefusals.TryGetValue(seatId, out var refusal))
            {
                return new HoldSeatResponse(seatId, refusal);
            }

            var expiry = HoldsExpiringAt.TryGetValue(seatId, out var at)
                ? at
                : Now.AddMinutes(5);

            return new HoldSeatResponse(seatId, HoldSeatStatus.Held, expiry);
        }
    }

    /// <summary>Payments faked at its contract; each answer can be changed between calls.</summary>
    private sealed class FakeOrderPayments : IOrderPayments
    {
        public AuthorizePaymentStatus AuthorizeWith { get; set; } = AuthorizePaymentStatus.Authorized;

        public CapturePaymentStatus CaptureWith { get; set; } = CapturePaymentStatus.Captured;

        public VoidPaymentStatus VoidWith { get; set; } = VoidPaymentStatus.Voided;

        public List<AuthorizePaymentRequest> Authorizations { get; } = [];

        public List<CapturePaymentRequest> Captures { get; } = [];

        public List<VoidPaymentRequest> Voids { get; } = [];

        public Task<AuthorizePaymentResponse> AuthorizeAsync(
            AuthorizePaymentRequest request,
            CancellationToken cancellationToken = default)
        {
            Authorizations.Add(request);
            return Task.FromResult(new AuthorizePaymentResponse(AuthorizeWith, Guid.NewGuid()));
        }

        public Task<CapturePaymentResponse> CaptureAsync(
            CapturePaymentRequest request,
            CancellationToken cancellationToken = default)
        {
            Captures.Add(request);
            return Task.FromResult(new CapturePaymentResponse(CaptureWith, Guid.NewGuid()));
        }

        public Task<VoidPaymentResponse> VoidAsync(
            VoidPaymentRequest request,
            CancellationToken cancellationToken = default)
        {
            Voids.Add(request);
            return Task.FromResult(new VoidPaymentResponse(VoidWith, Guid.NewGuid()));
        }
    }

    private enum SeatState
    {
        Held,
        Available,
        Sold
    }

    private enum MoneyState
    {
        Nothing,
        Authorized,
        Voided,
        Captured
    }

    /// <summary>
    /// Inventory as state for one order, so a cancel can run between two of a confirm's steps
    /// and the ending can be read off the seats.
    /// </summary>
    private sealed class StatefulSeatReservations(Order order) : ISeatReservations
    {
        public Dictionary<Guid, SeatState> Seats { get; } =
            order.Lines.ToDictionary(line => line.SeatId, _ => SeatState.Held);

        /// <summary>Runs once, before the next sale looks at any seat.</summary>
        public Func<Task>? BeforeSell { get; set; }

        public Task<HoldSeatsResponse> HoldAsync(
            HoldSeatsRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The order is opened by the canned fake.");

        public async Task<SellSeatsResponse> SellAsync(
            SellSeatsRequest request,
            CancellationToken cancellationToken = default)
        {
            if (BeforeSell is { } hook)
            {
                BeforeSell = null;
                await hook();
            }

            // Sold to this same client answers as sold, as the real handler does.
            var refusals = request.SeatIds
                .Where(seatId => Seats[seatId] is SeatState.Available)
                .Select(seatId => new SellSeatResponse(seatId, SellSeatStatus.NoActiveHold))
                .ToList();

            if (refusals.Count is 0)
            {
                foreach (var seatId in request.SeatIds)
                {
                    Seats[seatId] = SeatState.Sold;
                }
            }

            return new SellSeatsResponse(refusals);
        }

        public Task<ReleaseSeatsResponse> ReleaseAsync(
            ReleaseSeatsRequest request,
            CancellationToken cancellationToken = default)
        {
            var answers = new List<ReleaseSeatResponse>();

            foreach (var seatId in request.SeatIds)
            {
                if (Seats[seatId] is SeatState.Sold)
                {
                    answers.Add(new ReleaseSeatResponse(seatId, ReleaseSeatStatus.SoldToYou));
                    continue;
                }

                Seats[seatId] = SeatState.Available;
                answers.Add(new ReleaseSeatResponse(seatId, ReleaseSeatStatus.Released));
            }

            return Task.FromResult(new ReleaseSeatsResponse(answers));
        }
    }

    /// <summary>Payments as state: one attempt that a void releases and a capture takes.</summary>
    private sealed class StatefulOrderPayments : IOrderPayments
    {
        public MoneyState Money { get; private set; } = MoneyState.Nothing;

        /// <summary>Runs once, before the next capture looks at the money.</summary>
        public Func<Task>? BeforeCapture { get; set; }

        public Task<AuthorizePaymentResponse> AuthorizeAsync(
            AuthorizePaymentRequest request,
            CancellationToken cancellationToken = default)
        {
            if (Money is MoneyState.Captured)
            {
                return Task.FromResult(new AuthorizePaymentResponse(AuthorizePaymentStatus.AlreadyCaptured, Guid.NewGuid()));
            }

            Money = MoneyState.Authorized;
            return Task.FromResult(new AuthorizePaymentResponse(AuthorizePaymentStatus.Authorized, Guid.NewGuid()));
        }

        public async Task<CapturePaymentResponse> CaptureAsync(
            CapturePaymentRequest request,
            CancellationToken cancellationToken = default)
        {
            if (BeforeCapture is { } hook)
            {
                BeforeCapture = null;
                await hook();
            }

            if (Money is MoneyState.Authorized or MoneyState.Captured)
            {
                Money = MoneyState.Captured;
                return new CapturePaymentResponse(CapturePaymentStatus.Captured, Guid.NewGuid());
            }

            return new CapturePaymentResponse(CapturePaymentStatus.NoAuthorization, Guid.NewGuid());
        }

        public Task<VoidPaymentResponse> VoidAsync(
            VoidPaymentRequest request,
            CancellationToken cancellationToken = default)
        {
            switch (Money)
            {
                case MoneyState.Captured:
                    return Task.FromResult(new VoidPaymentResponse(VoidPaymentStatus.AlreadyCaptured, Guid.NewGuid()));

                case MoneyState.Authorized:
                    Money = MoneyState.Voided;
                    return Task.FromResult(new VoidPaymentResponse(VoidPaymentStatus.Voided, Guid.NewGuid()));

                default:
                    return Task.FromResult(new VoidPaymentResponse(VoidPaymentStatus.NoAuthorization, Guid.NewGuid()));
            }
        }
    }
}
