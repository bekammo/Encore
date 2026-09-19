using Encore.Modules.Catalog.Contracts;
using Encore.Modules.Inventory.Contracts;
using Encore.Modules.Orders.Data;
using Encore.Modules.Orders.Models;
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
/// partial unique index is load-bearing; Catalog and Inventory are faked because
/// they are reached through published contracts, and a test that needed all
/// three schemas migrated to check one refusal would be evidence those contracts
/// were not doing their job.
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
        Assert.Equal(3, seats.Holds.Count);

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
    /// Some sold and some did not. There is no automatic recovery, because there
    /// cannot be one: a sold seat is terminal, so nothing can un-sell the half
    /// that worked. A person has to look.
    /// </summary>
    [Fact]
    public async Task Confirm_WhenSomeSeatsSellAndOthersDoNot_ShouldFailTheOrder()
    {
        var clientId = Guid.NewGuid();
        var order = await AnOpenOrderAsync(clientId, seatCount: 2);
        var lost = order.Lines[1].SeatId;

        var seats = new FakeSeatReservations();
        seats.SellRefusals[lost] = SellSeatStatus.HoldExpired;

        await using var context = new OrdersDbContext(_options);
        var result = await ServiceFor(context, seats).ConfirmAsync(clientId, order.Id);

        Assert.Equal(OrderStatus.Failed, result.Order!.Status);
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

    // -- Cancel -----------------------------------------------------------

    /// <summary>
    /// Cancelling ends the order. It deliberately does not release the seats —
    /// see the remarks on <c>CheckoutService.CancelAsync</c>; that is an open
    /// question rather than a settled rule, and this test pins the current
    /// answer so changing it is a decision rather than a drift.
    /// </summary>
    [Fact]
    public async Task Cancel_ShouldEndTheOrderAndNotReleaseTheSeats()
    {
        var clientId = Guid.NewGuid();
        var order = await AnOpenOrderAsync(clientId, seatCount: 2);

        var seats = new FakeSeatReservations();

        await using var context = new OrdersDbContext(_options);
        var result = await ServiceFor(context, seats).CancelAsync(clientId, order.Id);

        Assert.Equal(OrderActionOutcome.Completed, result.Outcome);
        Assert.Equal(OrderStatus.Cancelled, result.Order!.Status);
        Assert.Equal(Now, result.Order.ClosedAt);
        Assert.Null(result.Order.HoldsExpireAt);
        Assert.Empty(seats.Releases);
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

    // -- Helpers ----------------------------------------------------------

    private CheckoutService ServiceFor(
        OrdersDbContext context,
        FakeSeatReservations seats,
        FakeEventPricing? pricing = null,
        DateTime? at = null) =>
        new(
            context,
            pricing ?? new FakeEventPricing(),
            seats,
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
    /// succeed unless a test names a seat that should not.
    /// </summary>
    private sealed class FakeSeatReservations : ISeatReservations
    {
        public Dictionary<Guid, DateTime> HoldsExpiringAt { get; } = [];

        public Dictionary<Guid, HoldSeatStatus> HoldRefusals { get; } = [];

        public Dictionary<Guid, SellSeatStatus> SellRefusals { get; } = [];

        public SellSeatStatus DefaultSell { get; set; } = SellSeatStatus.Sold;

        public List<HoldSeatRequest> Holds { get; } = [];

        public List<SellSeatRequest> Sells { get; } = [];

        public List<ReleaseSeatRequest> Releases { get; } = [];

        public Task<HoldSeatResponse> HoldAsync(
            HoldSeatRequest request,
            CancellationToken cancellationToken = default)
        {
            Holds.Add(request);

            if (HoldRefusals.TryGetValue(request.SeatId, out var refusal))
            {
                return Task.FromResult(new HoldSeatResponse(refusal));
            }

            var expiry = HoldsExpiringAt.TryGetValue(request.SeatId, out var at)
                ? at
                : Now.AddMinutes(5);

            return Task.FromResult(new HoldSeatResponse(HoldSeatStatus.Held, expiry));
        }

        public Task<ReleaseSeatResponse> ReleaseAsync(
            ReleaseSeatRequest request,
            CancellationToken cancellationToken = default)
        {
            Releases.Add(request);
            return Task.FromResult(new ReleaseSeatResponse(ReleaseSeatStatus.Released));
        }

        public Task<SellSeatResponse> SellAsync(
            SellSeatRequest request,
            CancellationToken cancellationToken = default)
        {
            Sells.Add(request);

            var status = SellRefusals.TryGetValue(request.SeatId, out var refusal)
                ? refusal
                : DefaultSell;

            return Task.FromResult(new SellSeatResponse(status));
        }
    }
}
