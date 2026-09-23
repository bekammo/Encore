using Encore.Modules.Catalog.Contracts;
using Encore.Modules.Inventory.Contracts;
using Encore.Modules.Orders.Data;
using Encore.Modules.Orders.Models;
using Encore.Modules.Payments.Contracts;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Orders.IntegrationTests;

/// <summary>
/// The checkout, confirm and cancel flows against real Postgres, with the two
/// modules this one depends on faked.
/// </summary>
/// <remarks>
/// <para>
/// <b>Postgres is real and the neighbours are not, which is the point.</b> The
/// database is here because this module declined a repository port and the
/// partial unique index is load-bearing; Catalog, Inventory and Payments are
/// faked because they are reached through published contracts, and a test that
/// needed all four schemas migrated to check one refusal would be evidence those
/// contracts were not doing their job.
/// </para>
/// <para>
/// Every test uses fresh client and event ids. The container is per-class and
/// the rows accumulate, so shared ids would collide on the one-open-checkout
/// index and fail the next test rather than the one with the bug in it.
/// </para>
/// </remarks>
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

    /// <summary>
    /// The rule from 023, and the one most likely to be "fixed" later into a
    /// compensating release. A partial checkout writes nothing and gives nothing
    /// back, so the customer can buy the rest or pick a replacement.
    /// </summary>
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

    /// <summary>
    /// Every seat is attempted even after the first refusal, so a client learns
    /// about all of them in one round trip and can choose replacements once.
    /// </summary>
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

    /// <summary>
    /// 025: a repeated id is refused rather than collapsed. Two lines for one
    /// seat is not a thing, and guessing which was meant is how you stop finding
    /// out the client has a bug.
    /// </summary>
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

    /// <summary>
    /// Duplicates are judged before the cap, so five ids naming one seat is a
    /// request for one seat rather than one seat over the limit.
    /// </summary>
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

    /// <summary>
    /// The cap is read from the contract, which is what 020 published it for: the
    /// alternative is sending five holds and compensating four of them to learn a
    /// number that was never a secret.
    /// </summary>
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

    /// <summary>
    /// 026. Catalog states the sale window and this module enforces it, because
    /// Catalog has no idea what a checkout is.
    /// </summary>
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

    /// <summary>
    /// Sales stay open once a show has started, because walk-up sales are real.
    /// The gate is the lower bound only.
    /// </summary>
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

    /// <summary>
    /// An event with no sale window is on sale immediately, which is what null
    /// means rather than "a gate that opened in the year 1".
    /// </summary>
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

    /// <summary>
    /// A pending order at one event must not block a checkout at another: the
    /// index is scoped to the pair, and this is what would catch a filter that
    /// had quietly lost its event column.
    /// </summary>
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
    /// The test 021 names by name, and the reason the rule is written down.
    /// </summary>
    /// <remarks>
    /// The clock is far past <c>HoldsExpireAt</c> and Inventory says the seat
    /// sold. The order must confirm. Two copies of the expiry rule judged against
    /// two clocks is a system that tells a customer their hold has gone while the
    /// seat is still theirs — so this module does not get a copy. The day somebody
    /// adds an expiry check to the confirm path, this test fails.
    /// </remarks>
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

    /// <summary>
    /// Nothing sold and every refusal an expiry: the customer ran out of time,
    /// which is a different thing to tell them than "something went wrong".
    /// </summary>
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
    /// The case 028 had to leave for a person to look at, and 076 closed. One
    /// hold has lapsed and the other is live. Sold one at a time, the live seat
    /// sold and stayed sold with nobody paying for it; now the seats are asked
    /// for together, in one request, and the order ends with nothing sold.
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

        // Every refusal was an expiry, so this is the ordinary ending rather than
        // a failure — the seat that was still live never sold.
        Assert.Equal(OrderStatus.Expired, result.Order!.Status);
    }

    /// <summary>
    /// A refusal that was not expiry is <c>Failed</c> even when nothing sold:
    /// "somebody else bought your seat" is not "you ran out of time".
    /// </summary>
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

    /// <summary>
    /// A retried confirm after a dropped response must not tell a client its
    /// completed order failed.
    /// </summary>
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

    /// <summary>
    /// The ordering that the whole of 028 rests on. A sold seat is terminal and
    /// cannot be given back, so no seat is sold until the money is secured — and
    /// when it is not secured, not one sell request goes out.
    /// </summary>
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

        // The holds are still live and the order is still completable, which is
        // the entire reason a decline does not end it.
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

    /// <summary>
    /// The benign failure the whole two-phase arrangement buys. The sale did not
    /// complete, so the hold on the customer's money is released and they never
    /// see a charge — not a charge followed by a refund.
    /// </summary>
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

    /// <summary>
    /// One seat of two refuses for a reason that is not expiry. Nothing sold, so
    /// the order is <c>Failed</c> and the customer is charged nothing — which is
    /// no longer a refund for seats that were kept, because none were.
    /// </summary>
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

    /// <summary>
    /// Seats sold, funds held, capture unanswered. Not an ending — the customer
    /// has their tickets and the only thing outstanding is ours to finish — so the
    /// order is dated as still going somewhere. See 027.
    /// </summary>
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

    /// <summary>
    /// The resolve-on-next-touch rule that lets 027 exist without a background
    /// job. The retry captures and nothing else: the seats are already sold, and
    /// asking Inventory again would be round trips against the hottest rows in the
    /// system for an answer nobody needs.
    /// </summary>
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

    /// <summary>
    /// Seats sold and nothing held against them. Reachable only if the
    /// authorisation went away underneath the confirm, which is exactly the shape
    /// of problem <c>Failed</c> exists to name.
    /// </summary>
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

    /// <summary>
    /// A previous confirm captured and then failed to record the order. Carrying
    /// on is what heals it: the sells are idempotent for the client that already
    /// bought, and the capture answers Captured a second time.
    /// </summary>
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

    /// <summary>
    /// Cancelling ends the order and hands both the seats and the money back.
    /// </summary>
    /// <remarks>
    /// The release is 034, and it is the reversal of what this test used to pin.
    /// 021 forbids releasing seats <i>because a hold lapsed</i> — a second
    /// authority over expiry — and a customer deliberately cancelling is not that.
    /// Until this changed, <c>SeatReleaseReason.Cancelled</c> had no producer at
    /// all.
    /// </remarks>
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
    /// A confirm has sold the seats and this client owns them. Voiding now would
    /// take back the money for a sale that stands, so this touches neither the
    /// money nor the order and lets the retry find the order as it actually
    /// stands. 077.
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

    /// <summary>
    /// A seat sold to somebody else is not a confirm of this order winning — the
    /// client's hold lapsed and another buyer took it — so it does not stop the
    /// cancellation, and the money goes back.
    /// </summary>
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
    /// Unreachable by any interleaving of this module's own confirm and cancel —
    /// money is captured only after every seat sold, and a sold seat answers
    /// <see cref="ReleaseSeatStatus.SoldToYou"/> before the void is asked. Kept
    /// as a defence: if Payments ever says the money was taken, a cancellation
    /// is not written over it.
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

    /// <summary>
    /// Ending an order frees the client to start another one for the same event,
    /// which is what makes the partial index partial.
    /// </summary>
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

    // -- Confirm and cancel together (DECISIONS 077) ----------------------

    /// <summary>
    /// The interleaving 077 closes. A confirm has authorised and sold, and before
    /// it captures, a cancel runs start to finish. When cancel voided first, it
    /// released the authorisation the capture was about to take: the seats stayed
    /// sold, the capture found nothing, and a customer held seats nobody paid for.
    /// Now cancel asks about the seats first, hears that its own confirm sold
    /// them, and leaves the money where it is.
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

        // The invariant, stated before the outcomes: sold seats and taken money
        // go together.
        Assert.All(seats.Seats.Values, seat => Assert.Equal(SeatState.Sold, seat));
        Assert.Equal(MoneyState.Captured, payments.Money);

        Assert.Equal(OrderActionOutcome.LostRace, cancel!.Outcome);
        Assert.Equal(OrderActionOutcome.Completed, confirm.Outcome);

        await using var reader = new OrdersDbContext(_options);
        var stored = await reader.Orders.SingleAsync(candidate => candidate.Id == order.Id);
        Assert.Equal(OrderStatus.Confirmed, stored.Status);
    }

    /// <summary>
    /// The other side of the same race: the cancel lands after the confirm has
    /// authorised but before it sells. The seats go back first, so the sale finds
    /// nothing to sell, and both halves release the money. Nothing sold, nothing
    /// taken, and the cancel's ending is the one written.
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

    /// <summary>
    /// A pending order with the given number of seats, committed and detached.
    /// </summary>
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

    /// <summary>
    /// Catalog, faked at its published contract. Priced and on sale unless a test
    /// says otherwise.
    /// </summary>
    private sealed class FakeEventPricing : IEventPricing
    {
        public EventPricingResponse Response { get; set; } =
            EventPricingResponse.Priced(UnitPrice, Currency, OnSale, Starts);

        public Task<EventPricingResponse> GetAsync(
            EventPricingRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Response);
    }

    /// <summary>
    /// Inventory, faked at its published contract. Holds succeed and sales
    /// succeed unless a test names a seat that should not — and, as the contract
    /// promises, one refusing seat means none sell.
    /// </summary>
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

    /// <summary>
    /// Payments, faked at its published contract. Everything works unless a test
    /// says otherwise, and each answer is settable between calls so a test can
    /// make the first capture hang and the retry succeed.
    /// </summary>
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
    /// Inventory as state rather than as canned answers, for one client's order:
    /// a sale moves every held seat to sold or none, and a release answers
    /// whatever the seat's state now is. That is what lets a cancel be run
    /// between two of a confirm's steps and the ending read off the seats.
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

    /// <summary>
    /// Payments as state: one attempt, whose authorisation a void releases and a
    /// capture takes — and a capture of something voided finds nothing to take.
    /// </summary>
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
