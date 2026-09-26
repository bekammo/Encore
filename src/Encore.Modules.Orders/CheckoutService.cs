using Encore.Modules.Catalog.Contracts;
using Encore.Modules.Inventory.Contracts;
using Encore.Modules.Orders.Data;
using Encore.Modules.Orders.Models;
using Encore.Modules.Payments.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Encore.Modules.Orders;

public sealed class CheckoutService(
    OrdersDbContext orders,
    IEventPricing pricing,
    ISeatReservations seats,
    IOrderPayments payments,
    TimeProvider timeProvider)
{
    private readonly OrdersDbContext _orders = orders;
    private readonly IEventPricing _pricing = pricing;
    private readonly ISeatReservations _seats = seats;
    private readonly IOrderPayments _payments = payments;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <summary>
    /// Cheap checks run before the holds, which are writes against the hottest rows. If any seat
    /// is refused, nothing is written and the seats that were held stay held (009).
    /// </summary>
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

        // Before the cap check, so duplicates are reported as themselves.
        if (seatIds.Distinct().Count() != seatIds.Count)
        {
            return CheckoutResult.Refused(CheckoutOutcome.DuplicateSeat);
        }

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

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        if (priced.OnSaleAt is { } onSaleAt && utcNow < onSaleAt)
        {
            return CheckoutResult.Refused(CheckoutOutcome.NotOnSale);
        }

        // A courtesy read; the partial unique index is the real guard.
        if (await OpenCheckoutAsync(clientId, eventId, cancellationToken).ConfigureAwait(false)
            is { } openOrderId)
        {
            return CheckoutResult.AlreadyOpen(openOrderId);
        }

        var holds = await _seats
            .HoldAsync(new HoldSeatsRequest(eventId, seatIds, clientId), cancellationToken)
            .ConfigureAwait(false);

        var refusals = holds.Seats
            .Where(seat => seat.Status is not HoldSeatStatus.Held)
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
            return CheckoutResult.AlreadyOpen(
                await OpenCheckoutAsync(clientId, eventId, cancellationToken).ConfigureAwait(false));
        }

        return CheckoutResult.Created(order);
    }

    private Task<Guid?> OpenCheckoutAsync(Guid clientId, Guid eventId, CancellationToken cancellationToken) =>
        _orders.Orders
            .AsNoTracking()
            .Where(order => order.ClientId == clientId
                && order.EventId == eventId
                && order.Status == OrderStatus.Pending)
            .Select(order => (Guid?)order.Id)
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Authorises, sells, then captures: a sold seat cannot be taken back and money can (010).
    /// Only the load honours <paramref name="cancellationToken"/>: a client hanging up between an
    /// authorisation and its void would leave funds held that nothing releases (022).
    /// </summary>
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

        // Expired too, so a confirm after the expiry sweep answers exactly as one that found the
        // holds lapsed itself (031).
        if (order.Status is OrderStatus.Confirmed or OrderStatus.Expired)
        {
            return new OrderActionResult(OrderActionOutcome.Completed, order);
        }

        if (order.Status is OrderStatus.AwaitingCapture)
        {
            return await CaptureAsync(order, clientId).ConfigureAwait(false);
        }

        if (order.Status is OrderStatus.PaymentDue)
        {
            return await PayAgainAsync(order, clientId).ConfigureAwait(false);
        }

        if (order.Status is not OrderStatus.Pending)
        {
            return new OrderActionResult(OrderActionOutcome.NotPending, order);
        }

        var authorized = await _payments
            .AuthorizeAsync(
                new AuthorizePaymentRequest(order.Id, clientId, order.Total, order.Currency),
                CancellationToken.None)
            .ConfigureAwait(false);

        OrderActionOutcome? refusal = authorized.Status switch
        {
            AuthorizePaymentStatus.Authorized => null,

            // An earlier confirm captured but did not record the order; carrying on heals it.
            AuthorizePaymentStatus.AlreadyCaptured => null,

            // A decline or timeout does not end the order: it and its holds stay, so the customer
            // can try again (010).
            AuthorizePaymentStatus.Declined => OrderActionOutcome.PaymentDeclined,
            AuthorizePaymentStatus.TimedOut => OrderActionOutcome.PaymentTimedOut,
            AuthorizePaymentStatus.ConcurrentAttemptInFlight => OrderActionOutcome.LostRace
        };

        if (refusal is { } outcome)
        {
            return new OrderActionResult(outcome, order);
        }

        var sale = await _seats
            .SellAsync(
                new SellSeatsRequest(order.EventId, [.. order.Lines.Select(line => line.SeatId)], clientId),
                CancellationToken.None)
            .ConfigureAwait(false);

        if (sale.AllSold)
        {
            // Recorded before the capture is asked for: a confirm that dies now leaves an order
            // anyone can see is owed its money, and the capture sweep finishes it (025).
            order.Status = OrderStatus.AwaitingCapture;
            order.HoldsExpireAt = null;
            order.SoldAt = _timeProvider.GetUtcNow().UtcDateTime;

            if (!await TrySaveAsync().ConfigureAwait(false))
            {
                // A second confirm of this order recorded the sale first, and owns the capture.
                return new OrderActionResult(OrderActionOutcome.LostRace, order);
            }

            return await CaptureAsync(order, clientId).ConfigureAwait(false);
        }

        // Nothing sold: a sale is all or none (011). Holds still live stay the client's and lapse
        // on their own.
        order.Status = sale.Refusals.All(seat => seat.Status is SellSeatStatus.HoldExpired)
            ? OrderStatus.Expired
            : OrderStatus.Failed;

        // The answer is ignored: an authorisation the void never reached lapses at the gateway.
        await _payments
            .VoidAsync(new VoidPaymentRequest(order.Id, clientId), CancellationToken.None)
            .ConfigureAwait(false);

        return await CloseAsync(order).ConfigureAwait(false);
    }

    private async Task<OrderActionResult> CaptureAsync(Order order, Guid clientId)
    {
        var captured = await _payments
            .CaptureAsync(new CapturePaymentRequest(order.Id, clientId), CancellationToken.None)
            .ConfigureAwait(false);

        order.Status = captured.Status switch
        {
            CapturePaymentStatus.Captured => OrderStatus.Confirmed,
            CapturePaymentStatus.TimedOut => OrderStatus.AwaitingCapture,

            // Every seat is sold, so the order is owed its money, never Failed (034).
            CapturePaymentStatus.Declined or CapturePaymentStatus.NoAuthorization => OrderStatus.PaymentDue
        };

        var closed = await CloseAsync(order).ConfigureAwait(false);

        return closed.Outcome is OrderActionOutcome.Completed && order.Status is OrderStatus.PaymentDue
            ? new OrderActionResult(OrderActionOutcome.PaymentDue, order)
            : closed;
    }

    // The seats are sold and stay sold (003), so only the money is asked for again. Nothing but
    // the customer's own confirm comes here: a sweep would be charging them unasked (034).
    private async Task<OrderActionResult> PayAgainAsync(Order order, Guid clientId)
    {
        var authorized = await _payments
            .AuthorizeAsync(
                new AuthorizePaymentRequest(order.Id, clientId, order.Total, order.Currency),
                CancellationToken.None)
            .ConfigureAwait(false);

        return authorized.Status switch
        {
            AuthorizePaymentStatus.Authorized or AuthorizePaymentStatus.AlreadyCaptured =>
                await CaptureAsync(order, clientId).ConfigureAwait(false),

            AuthorizePaymentStatus.Declined or AuthorizePaymentStatus.TimedOut =>
                new OrderActionResult(OrderActionOutcome.PaymentDue, order),

            AuthorizePaymentStatus.ConcurrentAttemptInFlight =>
                new OrderActionResult(OrderActionOutcome.LostRace, order)
        };
    }

    /// <summary>
    /// Releases the seats before the money, so no sale can follow the void (012). Only the load
    /// honours <paramref name="cancellationToken"/>: released seats with the money still held is
    /// the state a cancel exists to prevent (022).
    /// </summary>
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

        var release = await ReleaseSeatsAsync(order, clientId).ConfigureAwait(false);

        if (release.Seats.Any(seat => seat.Status is ReleaseSeatStatus.SoldToYou))
        {
            // A confirm of this order sold the seats and has not recorded it yet, so the money
            // stays and nothing is voided: a retry finds the order awaiting capture or confirmed.
            return new OrderActionResult(OrderActionOutcome.LostRace, order);
        }

        return await VoidAndEndAsync(order, clientId, OrderStatus.Cancelled).ConfigureAwait(false);
    }

    /// <summary>
    /// The expiry sweep's ending for a <c>Pending</c> order whose holds lapsed (031). Cancel's
    /// order, seats before money, because only Inventory can fence a confirm whose sale is in
    /// flight: that sale commits in Inventory before the order row records it.
    /// </summary>
    internal async Task<OrderActionResult> ExpireAsync(Guid clientId, Guid orderId)
    {
        var order = await LoadAsync(clientId, orderId, CancellationToken.None).ConfigureAwait(false);

        if (order is null)
        {
            return new OrderActionResult(OrderActionOutcome.OrderNotFound);
        }

        if (order.Status is not OrderStatus.Pending)
        {
            return new OrderActionResult(OrderActionOutcome.NotPending, order);
        }

        var release = await ReleaseSeatsAsync(order, clientId).ConfigureAwait(false);

        if (release.Seats.Any(seat => seat.Status is ReleaseSeatStatus.SoldToYou))
        {
            // A confirm sold the seats and died before recording it. The sale stands, so finish
            // it as the customer's next confirm would (025).
            return await ConfirmAsync(clientId, orderId).ConfigureAwait(false);
        }

        if (release.Seats.Any(seat => seat.Status is ReleaseSeatStatus.LostRace))
        {
            // Unattended, so any doubt leaves the order for the next sweep.
            return new OrderActionResult(OrderActionOutcome.LostRace, order);
        }

        return await VoidAndEndAsync(order, clientId, OrderStatus.Expired).ConfigureAwait(false);
    }

    private Task<ReleaseSeatsResponse> ReleaseSeatsAsync(Order order, Guid clientId) =>
        _seats.ReleaseAsync(
            new ReleaseSeatsRequest(order.EventId, [.. order.Lines.Select(line => line.SeatId)], clientId),
            CancellationToken.None);

    private async Task<OrderActionResult> VoidAndEndAsync(Order order, Guid clientId, OrderStatus ending)
    {
        var released = await _payments
            .VoidAsync(new VoidPaymentRequest(order.Id, clientId), CancellationToken.None)
            .ConfigureAwait(false);

        if (released.Status is VoidPaymentStatus.AlreadyCaptured)
        {
            // Defensive, unreachable by this module's interleavings (012): never write an ending
            // over money that has been taken.
            return new OrderActionResult(OrderActionOutcome.LostRace, order);
        }

        order.Status = ending;

        return await CloseAsync(order).ConfigureAwait(false);
    }

    private Task<Order?> LoadAsync(Guid clientId, Guid orderId, CancellationToken cancellationToken) =>
        _orders.Orders
            .Include(order => order.Lines)
            .SingleOrDefaultAsync(
                order => order.Id == orderId && order.ClientId == clientId,
                cancellationToken);

    private async Task<OrderActionResult> CloseAsync(Order order)
    {
        if (order.Status is not (OrderStatus.AwaitingCapture or OrderStatus.PaymentDue))
        {
            order.ClosedAt = _timeProvider.GetUtcNow().UtcDateTime;
        }

        order.HoldsExpireAt = null;

        return await TrySaveAsync().ConfigureAwait(false)
            ? new OrderActionResult(OrderActionOutcome.Completed, order)
            : new OrderActionResult(OrderActionOutcome.LostRace, order);
    }

    private async Task<bool> TrySaveAsync()
    {
        try
        {
            await _orders.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    private static bool IsDuplicatePendingCheckout(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
        && postgres.ConstraintName == OrderConfiguration.PendingCheckoutIndex;
}
