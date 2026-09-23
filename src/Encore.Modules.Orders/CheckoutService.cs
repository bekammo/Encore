using Encore.Modules.Catalog.Contracts;
using Encore.Modules.Inventory.Contracts;
using Encore.Modules.Orders.Data;
using Encore.Modules.Orders.Models;
using Encore.Modules.Payments.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Encore.Modules.Orders;

/// <summary>
/// The whole of this module's behaviour: place a checkout, confirm it, cancel it.
/// </summary>
/// <remarks>
/// <para>
/// <b>It sits at the module root rather than in an <c>Application/</c> folder.</b>
/// Orders is a flat module (001) with exactly one service in it, and Catalog
/// already set the precedent for not growing a fourth folder to hold a single
/// thing. A folder here would be the first half of a layering this module has
/// no use for.
/// </para>
/// <para>
/// <b>It takes <see cref="OrdersDbContext"/> concretely.</b> There is no
/// <c>IOrderRepository</c>, because a port earns its place by buying
/// substitution and nothing here will ever be substituted. The three dependencies
/// that <i>are</i> interfaces — <see cref="IEventPricing"/>,
/// <see cref="ISeatReservations"/> and <see cref="IOrderPayments"/> — are
/// interfaces because they cross a module boundary and will one day cross a
/// process one, which is a different argument entirely.
/// </para>
/// <para>
/// <b>Money is secured before a seat is sold, never after.</b> A sold seat is
/// terminal (007) and there is no un-sell, so selling first and charging second
/// risks permanent, unrecoverable inventory loss; money is the one of the two
/// that can be given back. Authorise, sell, capture — and when the sale falls
/// over, void, so the customer never sees a charge at all. See 028.
/// </para>
/// <para>
/// <b>This module never judges expiry.</b> Nothing in this file compares
/// <see cref="Order.HoldsExpireAt"/> against the clock to decide anything.
/// Inventory owns the hold window and is asked; whatever it answers is what
/// happens. See <c>DECISIONS.md</c> 021, and the test named there.
/// </para>
/// </remarks>
public sealed class CheckoutService(
    OrdersDbContext orders,
    IEventPricing pricing,
    ISeatReservations seats,
    IOrderPayments payments,
    TimeProvider clock)
{
    /// <summary>The unique index that is the real guard against a duplicate checkout.</summary>
    private const string PendingCheckoutIndex = "ux_orders_client_event_pending";

    private readonly OrdersDbContext _orders = orders;
    private readonly IEventPricing _pricing = pricing;
    private readonly ISeatReservations _seats = seats;
    private readonly IOrderPayments _payments = payments;
    private readonly TimeProvider _clock = clock;

    /// <summary>
    /// Prices an event, holds the seats and records the order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order of the checks is not arbitrary. Everything knowable from the
    /// request alone is answered first, then Catalog, then the open-checkout
    /// courtesy read, and only then are any holds taken. Every step before the
    /// holds is free to a client that got it wrong; the holds are writes against
    /// the hottest rows in the system, and taking them before finding out the
    /// event does not exist would be paying that price for nothing.
    /// </para>
    /// <para>
    /// <b>A partial success writes nothing and releases nothing.</b> If any seat
    /// is refused, the seats that were held stay held and no order row is
    /// created, so the client can come back for the rest or pick a replacement
    /// without having lost what it already had. See <c>DECISIONS.md</c> 023.
    /// </para>
    /// </remarks>
    public async Task<CheckoutResult> CheckoutAsync(
        Guid clientId,
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seatIds);

        if (seatIds.Count is 0)
        {
            return CheckoutResult.Refused(CheckoutOutcome.NoSeats);
        }

        // Before the cap check, not after: five ids naming one seat is a request
        // for one seat, and counting the duplicates would refuse it for being too
        // large. Refusing outright rather than de-duplicating is 025.
        if (seatIds.Distinct().Count() != seatIds.Count)
        {
            return CheckoutResult.Refused(CheckoutOutcome.DuplicateSeat);
        }

        // Read from the contract rather than hard-coded, which is the entire
        // reason 020 published it. Inventory still judges the cap against its own
        // count of live holds, so HoldCapReached remains reachable below.
        if (seatIds.Count > SeatReservationLimits.MaxHoldsPerClientPerEvent)
        {
            return CheckoutResult.Refused(CheckoutOutcome.TooManySeats);
        }

        var priced = await _pricing
            .GetAsync(new EventPricingRequest(eventId), cancellationToken)
            .ConfigureAwait(false);

        if (priced.Status is EventPricingStatus.EventNotFound)
        {
            return CheckoutResult.Refused(CheckoutOutcome.EventNotFound);
        }

        // Time enters this method here and nowhere else in it.
        var utcNow = _clock.GetUtcNow().UtcDateTime;

        // Catalog states the sale window and does not enforce it, because it has
        // no idea what a checkout is. This is the enforcement. See 026.
        if (priced.OnSaleAt is { } onSaleAt && utcNow < onSaleAt)
        {
            return CheckoutResult.Refused(CheckoutOutcome.NotOnSale);
        }

        // A courtesy, not the guard. Two requests can both pass this read; the
        // partial unique index below is what actually says no.
        var alreadyOpen = await _orders.Orders
            .AsNoTracking()
            .AnyAsync(
                order => order.ClientId == clientId
                    && order.EventId == eventId
                    && order.Status == OrderStatus.Pending,
                cancellationToken)
            .ConfigureAwait(false);

        if (alreadyOpen)
        {
            return CheckoutResult.Refused(CheckoutOutcome.CheckoutAlreadyOpen);
        }

        // One call for every seat, and every seat answered. Inventory attempts
        // them all even when one is refused, so the client learns about every
        // unavailable seat and can choose replacements in a single round trip.
        var holds = await _seats
            .HoldAsync(new HoldSeatsRequest(eventId, seatIds, clientId), cancellationToken)
            .ConfigureAwait(false);

        var refusals = holds.Seats
            .Where(seat => seat.Status is not HoldSeatStatus.Held)
            .Select(seat => new SeatRefusal(seat.SeatId, seat.Status))
            .ToList();

        if (refusals.Count > 0)
        {
            return CheckoutResult.Unavailable(refusals);
        }

        var held = holds.Seats
            .Select(seat => (seat.SeatId, ExpiresAt: seat.HoldExpiresAt!.Value))
            .ToList();

        var unitPrice = priced.UnitPrice!.Value;
        var currency = priced.Currency!;

        var order = new Order
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            EventId = eventId,
            Status = OrderStatus.Pending,
            PlacedAt = utcNow,

            // The earliest, because an order needs all of its seats: the first
            // hold to lapse is when the order stops being completable. Copied
            // from what Inventory returned, never computed here.
            HoldsExpireAt = held.Min(seat => seat.ExpiresAt),

            Total = unitPrice * held.Count,
            Currency = currency,
            Lines = [.. held.Select(seat => new OrderLine
            {
                Id = Guid.NewGuid(),
                SeatId = seat.SeatId,
                UnitPrice = unitPrice,
                Currency = currency
            })]
        };

        _orders.Orders.Add(order);

        try
        {
            await _orders.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsDuplicatePendingCheckout(ex))
        {
            // The courtesy read above missed because a concurrent request from
            // this same client got there first. The database is the one that says
            // no, and it said no — which is the arrangement working.
            return CheckoutResult.Refused(CheckoutOutcome.CheckoutAlreadyOpen);
        }

        return CheckoutResult.Created(order);
    }

    /// <summary>
    /// Secures the money, converts the order's holds into sales, and then takes
    /// the money.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The order of the three steps is the design.</b> A sold seat is terminal
    /// (007), so selling before the funds are secured risks a seat that is gone
    /// forever against money that never arrives. An authorisation is the reverse:
    /// it can be released, and one nobody captures lapses at the gateway by
    /// itself. So the unrecoverable thing goes second, between two things that can
    /// be undone. See 028.
    /// </para>
    /// <para>
    /// <b>This method never refuses because <see cref="Order.HoldsExpireAt"/> has
    /// passed.</b> It asks Inventory and lets
    /// <see cref="SellSeatStatus.HoldExpired"/> be the thing that moves the order
    /// to <see cref="OrderStatus.Expired"/>. Two copies of the expiry rule judged
    /// against two clocks is a system that tells a customer their hold has gone
    /// while the seat is still theirs.
    /// </para>
    /// <para>
    /// The endings follow 021, with one addition. Every seat sold and the money
    /// taken is <see cref="OrderStatus.Confirmed"/>; every seat sold and the
    /// capture unanswered is <see cref="OrderStatus.AwaitingCapture"/>, which the
    /// next confirm resolves (027). Inventory sells every seat or none (076), so
    /// the only other ending is nothing sold: every refusal an expiry is
    /// <see cref="OrderStatus.Expired"/>, anything else is
    /// <see cref="OrderStatus.Failed"/>. Both release the authorisation, so an
    /// order that did not complete costs the customer nothing and leaves no seat
    /// sold without a buyer.
    /// </para>
    /// <para>
    /// A decline or a gateway timeout does <b>not</b> end the order. It stays
    /// <see cref="OrderStatus.Pending"/> with its holds intact, because the most
    /// ordinary payment failure there is should not cost a customer their seats.
    /// </para>
    /// </remarks>
    public async Task<OrderActionResult> ConfirmAsync(
        Guid clientId,
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var order = await LoadAsync(clientId, orderId, cancellationToken).ConfigureAwait(false);

        if (order is null)
        {
            return new OrderActionResult(OrderActionOutcome.OrderNotFound);
        }

        // Idempotent: a retried confirm after a dropped response must not tell a
        // client that its completed order failed.
        if (order.Status is OrderStatus.Confirmed)
        {
            return new OrderActionResult(OrderActionOutcome.Completed, order);
        }

        // The seats are already sold and only the money is outstanding, so this
        // retries the capture and nothing else. Selling again would be harmless —
        // Inventory answers Sold to the client that already bought — but it is a
        // transaction against the hottest rows in the system for an answer nobody
        // needs.
        if (order.Status is OrderStatus.AwaitingCapture)
        {
            return await CaptureAsync(order, clientId, cancellationToken).ConfigureAwait(false);
        }

        if (order.Status is not OrderStatus.Pending)
        {
            return new OrderActionResult(OrderActionOutcome.NotPending, order);
        }

        var authorized = await _payments
            .AuthorizeAsync(
                new AuthorizePaymentRequest(order.Id, clientId, order.Total, order.Currency),
                cancellationToken)
            .ConfigureAwait(false);

        switch (authorized.Status)
        {
            // Funds held, or already held by an attempt this one is a retry of.
            case AuthorizePaymentStatus.Authorized:
                break;

            // A previous confirm captured and then failed to record the order.
            // Carrying on is what heals it: the sells are idempotent for the
            // client that already bought, and the capture below answers Captured
            // a second time.
            case AuthorizePaymentStatus.AlreadyCaptured:
                break;

            // Neither of these touches the order. The holds stay live and the
            // customer can try again — with a different card, or with the same
            // question under the same key.
            case AuthorizePaymentStatus.Declined:
                return new OrderActionResult(OrderActionOutcome.PaymentDeclined, order);

            case AuthorizePaymentStatus.TimedOut:
                return new OrderActionResult(OrderActionOutcome.PaymentTimedOut, order);

            case AuthorizePaymentStatus.ConcurrentAttemptInFlight:
                return new OrderActionResult(OrderActionOutcome.LostRace, order);

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(order), authorized.Status, "Unmapped authorize status.");
        }

        // Every seat or none, in one transaction (076). Sold one at a time, an
        // order whose last hold had lapsed kept the seats before it sold and
        // refunded the customer for them — seats gone with nobody paying.
        var sale = await _seats
            .SellAsync(
                new SellSeatsRequest(order.EventId, [.. order.Lines.Select(line => line.SeatId)], clientId),
                cancellationToken)
            .ConfigureAwait(false);

        if (sale.AllSold)
        {
            return await CaptureAsync(order, clientId, cancellationToken).ConfigureAwait(false);
        }

        // Nothing sold. Every refusal an expiry is the ordinary ending; anything
        // else is a seat that stopped being this client's, which is what Failed
        // names. The holds that are still live stay live: the client can open a
        // new checkout with them and one replacement, as after a refused
        // checkout (023), and whatever it abandons lapses by itself.
        order.Status = sale.Refusals.All(refusal => refusal.Status is SellSeatStatus.HoldExpired)
            ? OrderStatus.Expired
            : OrderStatus.Failed;

        // The money goes back before anything else happens. Nothing is checked
        // about the answer: NoAuthorization means there was nothing to release,
        // and a timeout means the hold lapses at the gateway on its own. Neither
        // changes what this order is.
        await _payments
            .VoidAsync(new VoidPaymentRequest(order.Id, clientId), cancellationToken)
            .ConfigureAwait(false);

        return await CloseAsync(order, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes the money for an order whose seats are sold, and records which of the
    /// two endings that produced.
    /// </summary>
    /// <remarks>
    /// Reached from two places — the end of a confirm, and a confirm retried
    /// against an <see cref="OrderStatus.AwaitingCapture"/> order — because they
    /// want exactly the same thing and a second copy of this mapping is a second
    /// thing to keep correct.
    /// </remarks>
    private async Task<OrderActionResult> CaptureAsync(
        Order order,
        Guid clientId,
        CancellationToken cancellationToken)
    {
        var captured = await _payments
            .CaptureAsync(new CapturePaymentRequest(order.Id, clientId), cancellationToken)
            .ConfigureAwait(false);

        order.Status = captured.Status switch
        {
            CapturePaymentStatus.Captured => OrderStatus.Confirmed,

            // The seats are sold and the funds are still held. Not an ending, and
            // not a failure — the next confirm asks again (027).
            CapturePaymentStatus.TimedOut => OrderStatus.AwaitingCapture,

            // Seats sold and nothing held against them. Reachable only if the
            // authorisation went away underneath this confirm, which is the shape
            // of problem Failed exists to name.
            CapturePaymentStatus.NoAuthorization => OrderStatus.Failed,

            _ => throw new ArgumentOutOfRangeException(
                nameof(order), captured.Status, "Unmapped capture status.")
        };

        return await CloseAsync(order, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends an order because the customer said so, releasing both the money and
    /// the seats.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This releases the seats, and that used to be an open question.</b> 021
    /// forbids this module releasing seats <i>because a hold lapsed</i> — that
    /// would be a second authority over expiry, judged against Orders' clock. A
    /// customer saying "no thanks" is not a clock judgement, and until now
    /// <c>SeatReleaseReason.Cancelled</c> had no producer at all: a domain enum
    /// member nothing ever raised. During a flash sale, leaving up to four seats
    /// to lapse on their own after every cancellation strands the scarcest thing
    /// in the system for five minutes at a time. See 034.
    /// </para>
    /// <para>
    /// <b>The seats go back first, and the money only once they have.</b> This is
    /// 028's reasoning applied from the other end. A confirm can be anywhere
    /// between its authorisation and its capture while this runs, and the one
    /// thing that says whether it has passed the point of no return is the seats:
    /// once they are sold they stay sold. So this asks Inventory first. If any
    /// seat answers <see cref="ReleaseSeatStatus.SoldToYou"/>, a confirm of this
    /// order has sold them, the money is the only thing still to happen, and
    /// voiding it would leave a customer holding seats nobody paid for — the
    /// client is told to look again instead. If none does, no sale can follow,
    /// because the holds it would need are gone, and the money is safe to
    /// release. Voiding first — as this did until 077 — opened exactly that gap
    /// between a confirm's sale and its capture.
    /// </para>
    /// <para>
    /// Nothing else here checks whether a release succeeded. Any other refusal
    /// means the hold was already gone, which is the state this was asking for;
    /// Inventory reclaims lapsed holds lazily on every path regardless.
    /// </para>
    /// </remarks>
    public async Task<OrderActionResult> CancelAsync(
        Guid clientId,
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var order = await LoadAsync(clientId, orderId, cancellationToken).ConfigureAwait(false);

        if (order is null)
        {
            return new OrderActionResult(OrderActionOutcome.OrderNotFound);
        }

        if (order.Status is OrderStatus.Cancelled)
        {
            return new OrderActionResult(OrderActionOutcome.Completed, order);
        }

        if (order.Status is not OrderStatus.Pending)
        {
            return new OrderActionResult(OrderActionOutcome.NotPending, order);
        }

        var seats = await _seats
            .ReleaseAsync(
                new ReleaseSeatsRequest(order.EventId, [.. order.Lines.Select(line => line.SeatId)], clientId),
                cancellationToken)
            .ConfigureAwait(false);

        if (seats.Seats.Any(seat => seat.Status is ReleaseSeatStatus.SoldToYou))
        {
            // A confirm of this order has sold the seats and is about to take the
            // money, or took it and has not yet recorded so. Either way the order
            // is not this method's to end. The row still reads Pending in memory,
            // so saying NotPending would be reporting a status it has not read;
            // LostRace is the truth, and the retry finds the settled order.
            return new OrderActionResult(OrderActionOutcome.LostRace, order);
        }

        var released = await _payments
            .VoidAsync(new VoidPaymentRequest(order.Id, clientId), cancellationToken)
            .ConfigureAwait(false);

        if (released.Status is VoidPaymentStatus.AlreadyCaptured)
        {
            // Not reachable by this module's own confirm, which captures only after
            // every seat sold — and those answered SoldToYou above. Kept so that a
            // cancellation is never written over money Payments says it has taken.
            return new OrderActionResult(OrderActionOutcome.LostRace, order);
        }

        order.Status = OrderStatus.Cancelled;

        return await CloseAsync(order, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads an order and its lines, or nothing if it is not this client's.
    /// </summary>
    private Task<Order?> LoadAsync(Guid clientId, Guid orderId, CancellationToken cancellationToken) =>
        _orders.Orders
            .Include(order => order.Lines)
            .SingleOrDefaultAsync(
                order => order.Id == orderId && order.ClientId == clientId,
                cancellationToken);

    /// <summary>
    /// Stamps the outcome on an order whose status has just been decided, and
    /// saves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Order.HoldsExpireAt"/> is always cleared, because it describes
    /// an order that is still waiting on holds and none of these are: the seats
    /// have either been sold, released or lost. Leaving it would let a client
    /// render a countdown against an order that has nothing to count down to.
    /// </para>
    /// <para>
    /// <see cref="Order.ClosedAt"/> is stamped only for an ending.
    /// <see cref="OrderStatus.AwaitingCapture"/> is not one — the order is still
    /// going somewhere — and dating it as closed would make every report of
    /// completed orders quietly wrong.
    /// </para>
    /// </remarks>
    private async Task<OrderActionResult> CloseAsync(Order order, CancellationToken cancellationToken)
    {
        if (order.Status is not OrderStatus.AwaitingCapture)
        {
            order.ClosedAt = _clock.GetUtcNow().UtcDateTime;
        }

        order.HoldsExpireAt = null;

        try
        {
            await _orders.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Confirm and cancel arriving together. Inventory has protected the
            // seats either way; this protects this module's own record of what
            // happened.
            return new OrderActionResult(OrderActionOutcome.LostRace, order);
        }

        return new OrderActionResult(OrderActionOutcome.Completed, order);
    }

    /// <summary>
    /// Whether this save failed on the one-open-checkout index specifically,
    /// rather than on some other unique constraint that would be a real bug.
    /// </summary>
    private static bool IsDuplicatePendingCheckout(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: "23505" } postgres
        && postgres.ConstraintName == PendingCheckoutIndex;
}
