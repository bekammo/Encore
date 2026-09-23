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
/// Takes <see cref="OrdersDbContext"/> directly; only the cross-module dependencies are
/// interfaces. A confirm authorises, sells, then captures, because a sold seat cannot be
/// taken back and money can. Orders never judges hold expiry itself; it asks Inventory.
/// </remarks>
public sealed class CheckoutService(
    OrdersDbContext orders,
    IEventPricing pricing,
    ISeatReservations seats,
    IOrderPayments payments,
    TimeProvider clock)
{
    private readonly OrdersDbContext _orders = orders;
    private readonly IEventPricing _pricing = pricing;
    private readonly ISeatReservations _seats = seats;
    private readonly IOrderPayments _payments = payments;
    private readonly TimeProvider _clock = clock;

    /// <summary>
    /// Prices an event, holds the seats and records the order.
    /// </summary>
    /// <remarks>
    /// Cheap checks run before the holds, which are writes against the hottest rows. If
    /// any seat is refused, nothing is written and the seats that were held stay held.
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

        // Before the cap check, so duplicates are reported as themselves.
        if (seatIds.Distinct().Count() != seatIds.Count)
        {
            return CheckoutResult.Refused(CheckoutOutcome.DuplicateSeat);
        }

        // Inventory still enforces the cap itself, so HoldCapReached can still come back below.
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

        var utcNow = _clock.GetUtcNow().UtcDateTime;

        // Catalog states the on-sale time; Orders enforces it.
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

        // Every seat is answered, so the client learns about all unavailable seats at once.
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

            // The earliest expiry, copied from Inventory, never computed here.
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
            // A concurrent checkout by the same client won; the index refused this one.
            return CheckoutResult.AlreadyOpen(
                await OpenCheckoutAsync(clientId, eventId, cancellationToken).ConfigureAwait(false));
        }

        return CheckoutResult.Created(order);
    }

    /// <summary>The client's open checkout for this event, if there is one.</summary>
    private Task<Guid?> OpenCheckoutAsync(Guid clientId, Guid eventId, CancellationToken cancellationToken) =>
        _orders.Orders
            .AsNoTracking()
            .Where(order => order.ClientId == clientId
                && order.EventId == eventId
                && order.Status == OrderStatus.Pending)
            .Select(order => (Guid?)order.Id)
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Authorises the total, sells the seats, then captures.
    /// </summary>
    /// <remarks>
    /// All seats sold and captured is <see cref="OrderStatus.Confirmed"/>; sold but the
    /// capture unanswered is <see cref="OrderStatus.AwaitingCapture"/>, which the next
    /// confirm or the capture sweep resolves. The sale is recorded as awaiting capture before
    /// the capture is asked for, so a confirm that dies there leaves a findable order. Nothing
    /// sold ends <see cref="OrderStatus.Expired"/> or <see cref="OrderStatus.Failed"/> and
    /// voids the authorisation. A decline or payment timeout leaves the order
    /// <see cref="OrderStatus.Pending"/> with its holds intact.
    /// Only the load honours <paramref name="cancellationToken"/>: every later step is
    /// irreversible or undoes one, and a client hanging up between an authorisation and its
    /// void would leave funds held that nothing releases.
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

        // Idempotent, so a retried confirm is not told its completed order failed.
        if (order.Status is OrderStatus.Confirmed)
        {
            return new OrderActionResult(OrderActionOutcome.Completed, order);
        }

        // Seats already sold: only the capture is retried.
        if (order.Status is OrderStatus.AwaitingCapture)
        {
            return await CaptureAsync(order, clientId).ConfigureAwait(false);
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
            // Funds held, now or by an earlier attempt.
            AuthorizePaymentStatus.Authorized => null,

            // An earlier confirm captured but did not record the order; carrying on heals it.
            AuthorizePaymentStatus.AlreadyCaptured => null,

            // The order and its holds stay as they are, so the customer can try again.
            AuthorizePaymentStatus.Declined => OrderActionOutcome.PaymentDeclined,
            AuthorizePaymentStatus.TimedOut => OrderActionOutcome.PaymentTimedOut,
            AuthorizePaymentStatus.ConcurrentAttemptInFlight => OrderActionOutcome.LostRace
        };

        if (refusal is { } outcome)
        {
            return new OrderActionResult(outcome, order);
        }

        // Every seat or none, in one transaction.
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
            order.SoldAt = _clock.GetUtcNow().UtcDateTime;

            if (!await TrySaveAsync().ConfigureAwait(false))
            {
                // A second confirm of this order recorded the sale first, and owns the capture.
                return new OrderActionResult(OrderActionOutcome.LostRace, order);
            }

            return await CaptureAsync(order, clientId).ConfigureAwait(false);
        }

        // Nothing sold. Holds that are still live stay the client's and lapse on their own.
        order.Status = sale.Refusals.All(refusal => refusal.Status is SellSeatStatus.HoldExpired)
            ? OrderStatus.Expired
            : OrderStatus.Failed;

        // Release the authorisation. Any answer is acceptable: an unanswered one lapses at the gateway.
        await _payments
            .VoidAsync(new VoidPaymentRequest(order.Id, clientId), CancellationToken.None)
            .ConfigureAwait(false);

        return await CloseAsync(order).ConfigureAwait(false);
    }

    /// <summary>
    /// Captures the money for an order whose seats are sold and records the ending. Not
    /// cancellable: the seats are already sold.
    /// </summary>
    private async Task<OrderActionResult> CaptureAsync(Order order, Guid clientId)
    {
        var captured = await _payments
            .CaptureAsync(new CapturePaymentRequest(order.Id, clientId), CancellationToken.None)
            .ConfigureAwait(false);

        order.Status = captured.Status switch
        {
            CapturePaymentStatus.Captured => OrderStatus.Confirmed,

            // Not an ending: the next confirm retries the capture.
            CapturePaymentStatus.TimedOut => OrderStatus.AwaitingCapture,

            // The authorisation vanished under this confirm; someone has to look.
            CapturePaymentStatus.NoAuthorization => OrderStatus.Failed
        };

        return await CloseAsync(order).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels an order, releasing the seats and then the money.
    /// </summary>
    /// <remarks>
    /// Seats first: if any answers <see cref="ReleaseSeatStatus.SoldToYou"/>, a confirm of
    /// this order has already sold them and the money must stay, so this backs off. Only
    /// when no sale can follow is the authorisation voided. As with a confirm, only the load
    /// honours <paramref name="cancellationToken"/>: released seats with the money still held
    /// is the state a cancel exists to prevent.
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
                CancellationToken.None)
            .ConfigureAwait(false);

        if (seats.Seats.Any(seat => seat.Status is ReleaseSeatStatus.SoldToYou))
        {
            // A confirm of this order sold the seats and has not recorded it yet. Report a lost
            // race: a retry finds the order awaiting capture or confirmed.
            return new OrderActionResult(OrderActionOutcome.LostRace, order);
        }

        var released = await _payments
            .VoidAsync(new VoidPaymentRequest(order.Id, clientId), CancellationToken.None)
            .ConfigureAwait(false);

        if (released.Status is VoidPaymentStatus.AlreadyCaptured)
        {
            // Defensive: never write a cancellation over money that has been taken.
            return new OrderActionResult(OrderActionOutcome.LostRace, order);
        }

        order.Status = OrderStatus.Cancelled;

        return await CloseAsync(order).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads an order and its lines, or null if it is not this client's.
    /// </summary>
    private Task<Order?> LoadAsync(Guid clientId, Guid orderId, CancellationToken cancellationToken) =>
        _orders.Orders
            .Include(order => order.Lines)
            .SingleOrDefaultAsync(
                order => order.Id == orderId && order.ClientId == clientId,
                cancellationToken);

    /// <summary>
    /// Saves a decided order. Clears <see cref="Order.HoldsExpireAt"/>, and stamps
    /// <see cref="Order.ClosedAt"/> only for a real ending (not AwaitingCapture).
    /// </summary>
    private async Task<OrderActionResult> CloseAsync(Order order)
    {
        if (order.Status is not OrderStatus.AwaitingCapture)
        {
            order.ClosedAt = _clock.GetUtcNow().UtcDateTime;
        }

        order.HoldsExpireAt = null;

        // False when a confirm and a cancel, or two confirms, raced on this row.
        return await TrySaveAsync().ConfigureAwait(false)
            ? new OrderActionResult(OrderActionOutcome.Completed, order)
            : new OrderActionResult(OrderActionOutcome.LostRace, order);
    }

    /// <summary>Saves the order, or reports that another writer changed the row first.</summary>
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

    /// <summary>
    /// Whether the save failed on the one-open-checkout index, not some other constraint.
    /// </summary>
    private static bool IsDuplicatePendingCheckout(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: "23505" } postgres
        && postgres.ConstraintName == OrderConfiguration.PendingCheckoutIndex;
}
