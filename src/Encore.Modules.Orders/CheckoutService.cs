using Encore.Modules.Catalog.Contracts;
using Encore.Modules.Inventory.Contracts;
using Encore.Modules.Orders.Data;
using Encore.Modules.Orders.Models;
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
/// substitution and nothing here will ever be substituted. The two dependencies
/// that <i>are</i> interfaces — <see cref="IEventPricing"/> and
/// <see cref="ISeatReservations"/> — are interfaces because they cross a module
/// boundary and will one day cross a process one, which is a different argument
/// entirely.
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
    TimeProvider clock)
{
    /// <summary>The unique index that is the real guard against a duplicate checkout.</summary>
    private const string PendingCheckoutIndex = "ux_orders_client_event_pending";

    private readonly OrdersDbContext _orders = orders;
    private readonly IEventPricing _pricing = pricing;
    private readonly ISeatReservations _seats = seats;
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

        var held = new List<(Guid SeatId, DateTime ExpiresAt)>(seatIds.Count);
        var refusals = new List<SeatRefusal>();

        // Every seat is attempted even after the first refusal. Stopping early
        // would be cheaper, and would tell the client about one unavailable seat
        // when it needs to know about all of them to choose replacements in a
        // single round trip.
        foreach (var seatId in seatIds)
        {
            var response = await _seats
                .HoldAsync(new HoldSeatRequest(eventId, seatId, clientId), cancellationToken)
                .ConfigureAwait(false);

            if (response.Status is HoldSeatStatus.Held)
            {
                held.Add((seatId, response.HoldExpiresAt!.Value));
            }
            else
            {
                refusals.Add(new SeatRefusal(seatId, response.Status));
            }
        }

        if (refusals.Count > 0)
        {
            return CheckoutResult.Unavailable(refusals);
        }

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
    /// Converts an order's holds into sales, and records what Inventory decided.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This method never refuses because <see cref="Order.HoldsExpireAt"/> has
    /// passed.</b> It asks Inventory and lets
    /// <see cref="SellSeatStatus.HoldExpired"/> be the thing that moves the order
    /// to <see cref="OrderStatus.Expired"/>. Two copies of the expiry rule judged
    /// against two clocks is a system that tells a customer their hold has gone
    /// while the seat is still theirs.
    /// </para>
    /// <para>
    /// The three endings follow 021 exactly. Every seat sold is
    /// <see cref="OrderStatus.Confirmed"/>. Nothing sold and every refusal an
    /// expiry is <see cref="OrderStatus.Expired"/>. Anything else — a mix, or a
    /// refusal that was not expiry — is <see cref="OrderStatus.Failed"/>, the
    /// status that means a person has to look. There is no automatic recovery
    /// from it because there cannot be one: a sold seat is terminal, so nothing
    /// can un-sell the half that worked.
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

        if (order.Status is not OrderStatus.Pending)
        {
            return new OrderActionResult(OrderActionOutcome.NotPending, order);
        }

        var sold = 0;
        var expired = 0;
        var otherRefusals = 0;

        foreach (var line in order.Lines)
        {
            var response = await _seats
                .SellAsync(new SellSeatRequest(order.EventId, line.SeatId, clientId), cancellationToken)
                .ConfigureAwait(false);

            switch (response.Status)
            {
                case SellSeatStatus.Sold:
                    sold++;
                    break;

                case SellSeatStatus.HoldExpired:
                    expired++;
                    break;

                case SellSeatStatus.AlreadySold:
                case SellSeatStatus.NotTheHolder:
                case SellSeatStatus.NoActiveHold:
                case SellSeatStatus.SeatNotFound:
                case SellSeatStatus.LostRace:
                    otherRefusals++;
                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(response), response.Status, "Unmapped sell status.");
            }
        }

        order.Status = (sold, expired, otherRefusals) switch
        {
            (_, 0, 0) => OrderStatus.Confirmed,
            (0, _, 0) => OrderStatus.Expired,
            _ => OrderStatus.Failed
        };

        return await CloseAsync(order, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends an order because the customer said so.
    /// </summary>
    /// <remarks>
    /// <b>This does not release the seats, and that is an open question rather
    /// than a settled rule.</b> 021 forbids this module releasing seats
    /// <i>because a hold lapsed</i>, which is a different thing from a customer
    /// cancelling deliberately — and the domain's own
    /// <c>SeatReleaseReason.Cancelled</c> has no other producer, which hints the
    /// other way. Until that is decided, this does the null thing: the holds are
    /// left to lapse on their own, which Inventory reclaims lazily on every read
    /// and write path. Untidy for up to five minutes, never incorrect.
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
    /// Stamps the ending on an order whose status has just been decided, and saves.
    /// </summary>
    /// <remarks>
    /// <see cref="Order.HoldsExpireAt"/> is cleared because it describes an order
    /// that can still be completed, and this one no longer can. Leaving it would
    /// let a client render a countdown against an order that has already ended.
    /// </remarks>
    private async Task<OrderActionResult> CloseAsync(Order order, CancellationToken cancellationToken)
    {
        order.ClosedAt = _clock.GetUtcNow().UtcDateTime;
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
