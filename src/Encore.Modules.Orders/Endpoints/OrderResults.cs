using Encore.Modules.Inventory.Contracts;
using Encore.Modules.Orders.Models;
using Microsoft.AspNetCore.Http;

namespace Encore.Modules.Orders.Endpoints;

/// <summary>
/// Maps service outcomes to HTTP responses. Refusals about the state of the world are 409;
/// a missing order is 404; a request wrong on its own terms (such as more seats than the
/// published cap) is 400. Every switch is exhaustive.
/// </summary>
internal static class OrderResults
{
    /// <summary>Maps the outcome of a checkout.</summary>
    public static IResult ForCheckout(CheckoutResult result, PathString path) =>
        result.Outcome switch
        {
            CheckoutOutcome.Created => TypedResults.Created(
                $"/orders/{result.Order!.Id}",
                OrderResponse.From(result.Order)),

            CheckoutOutcome.NoSeats => Invalid(
                path, "no_seats", "An order needs at least one seat."),

            CheckoutOutcome.DuplicateSeat => Invalid(
                path,
                "duplicate_seat",
                "The same seat was asked for more than once. A seat can be bought exactly once."),

            CheckoutOutcome.TooManySeats => Invalid(
                path,
                "too_many_seats",
                $"You may hold at most {SeatReservationLimits.MaxHoldsPerClientPerEvent} seats "
                + "at this event at once.",
                limit: SeatReservationLimits.MaxHoldsPerClientPerEvent),

            CheckoutOutcome.EventNotFound => Conflict(
                path, "event_not_found", "No event with that id.", retriable: false),

            // Becomes possible on its own once the sale opens.
            CheckoutOutcome.NotOnSale => Conflict(
                path, "not_on_sale", "Tickets for this event are not on sale yet.", retriable: true),

            CheckoutOutcome.CheckoutAlreadyOpen => AlreadyOpen(result.OpenOrderId, path),

            CheckoutOutcome.SeatsUnavailable => SeatsUnavailable(result.Refusals!, path)
        };

    /// <summary>Maps the outcome of a confirm.</summary>
    public static IResult ForConfirm(OrderActionResult result, PathString path) =>
        result.Outcome switch
        {
            OrderActionOutcome.Completed => ForEnding(result.Order!, path),
            _ => ForFailedAction(result, path)
        };

    /// <summary>Maps the outcome of a cancel.</summary>
    public static IResult ForCancel(OrderActionResult result, PathString path) =>
        result.Outcome switch
        {
            OrderActionOutcome.Completed => TypedResults.Ok(OrderResponse.From(result.Order!)),
            _ => ForFailedAction(result, path)
        };

    /// <summary>Renders an order that was read rather than acted on.</summary>
    public static IResult ForRead(Order order) => TypedResults.Ok(OrderResponse.From(order));

    /// <summary>The order is not there, or is not this client's. Deliberately the same answer.</summary>
    public static IResult NotFound(PathString path) =>
        TypedResults.Problem(
            detail: "No such order.",
            statusCode: StatusCodes.Status404NotFound,
            title: "Order not found",
            instance: path,
            extensions: new Dictionary<string, object?> { ["reason"] = "order_not_found" });

    /// <summary>
    /// A confirm ran; the ending is on the order.
    /// </summary>
    private static IResult ForEnding(Order order, PathString path) =>
        order.Status switch
        {
            OrderStatus.Confirmed => TypedResults.Ok(OrderResponse.From(order)),

            // 200: the customer has every seat; only the capture is outstanding.
            OrderStatus.AwaitingCapture => TypedResults.Ok(OrderResponse.From(order)),

            // Not retriable: the holds are gone; a new checkout is a different request.
            OrderStatus.Expired => Conflict(
                path,
                "holds_expired",
                "The holds on this order lapsed before it was confirmed.",
                retriable: false),

            OrderStatus.Failed => Conflict(
                path,
                "order_failed",
                "This order could not be completed and needs to be looked at.",
                retriable: false),

            // Confirm never leaves an order Pending or Cancelled; this would be a bug.
            OrderStatus.Pending or OrderStatus.Cancelled => throw new ArgumentOutOfRangeException(
                nameof(order), order.Status, "A completed action left the order in a non-terminal status.")
        };

    /// <summary>The shared refusals of confirm and cancel.</summary>
    private static IResult ForFailedAction(OrderActionResult result, PathString path) =>
        result.Outcome switch
        {
            OrderActionOutcome.OrderNotFound => NotFound(path),

            // Rendered through OrderResponse so the status is spelled the same everywhere.
            OrderActionOutcome.NotPending => Conflict(
                path,
                "order_not_pending",
                $"This order is {OrderResponse.From(result.Order!).Status} and cannot be changed.",
                retriable: false),

            OrderActionOutcome.LostRace => Conflict(
                path,
                "lost_race",
                "Another request for this order got there first. Try again to see how it ended.",
                retriable: true),

            // Retriable with a different card: the order and its holds are untouched.
            OrderActionOutcome.PaymentDeclined => Conflict(
                path,
                "payment_declined",
                "The payment was declined. Your seats are still held — try again.",
                retriable: true),

            OrderActionOutcome.PaymentTimedOut => Conflict(
                path,
                "payment_timed_out",
                "The payment provider did not answer in time. Your seats are still held, "
                + "and nothing has been charged that will not be released.",
                retriable: true),

            OrderActionOutcome.Completed => throw new ArgumentOutOfRangeException(
                nameof(result), result.Outcome, "Completed is not a failed action.")
        };

    /// <summary>
    /// One or more seats could not be held, so nothing was written. The top-level
    /// <c>retriable</c> is true only if every per-seat refusal is.
    /// </summary>
    private static IResult SeatsUnavailable(IReadOnlyList<HoldSeatResponse> refusals, PathString path)
    {
        var seats = refusals
            .Select(refusal => new
            {
                seatId = refusal.SeatId,
                reason = ReasonFor(refusal.Status),
                retriable = IsRetriable(refusal.Status)
            })
            .ToArray();

        return TypedResults.Problem(
            detail: "Some of the seats you asked for could not be held. "
                + "The seats that were held are still yours.",
            statusCode: StatusCodes.Status409Conflict,
            title: "Seats unavailable",
            instance: path,
            extensions: new Dictionary<string, object?>
            {
                ["reason"] = "seats_unavailable",
                ["retriable"] = refusals.All(refusal => IsRetriable(refusal.Status)),
                ["seats"] = seats
            });
    }

    /// <summary>
    /// Inventory's status as a reason string, in Inventory's own vocabulary.
    /// </summary>
    private static string ReasonFor(HoldSeatStatus status) =>
        status switch
        {
            HoldSeatStatus.AlreadyHeld => "already_held",
            HoldSeatStatus.AlreadySold => "already_sold",
            HoldSeatStatus.SeatNotFound => "seat_not_found",
            HoldSeatStatus.LostRace => "lost_race",
            HoldSeatStatus.HoldCapReached => "hold_cap_reached",
            HoldSeatStatus.ConcurrentRequestInFlight => "concurrent_request_in_flight",

            HoldSeatStatus.Held => throw new ArgumentOutOfRangeException(
                nameof(status), status, "A held seat is not a refusal.")
        };

    private static bool IsRetriable(HoldSeatStatus status) =>
        status switch
        {
            // Holds lapse, the cap frees up, and races are transient.
            HoldSeatStatus.AlreadyHeld => true,
            HoldSeatStatus.LostRace => true,
            HoldSeatStatus.HoldCapReached => true,
            HoldSeatStatus.ConcurrentRequestInFlight => true,

            // Sold is terminal, and a seat that does not exist will not start to.
            HoldSeatStatus.AlreadySold => false,
            HoldSeatStatus.SeatNotFound => false,

            HoldSeatStatus.Held => throw new ArgumentOutOfRangeException(
                nameof(status), status, "A held seat is not a refusal.")
        };

    /// <summary>
    /// Names the open checkout in <c>orderId</c>: nothing else lists a client's orders, so a
    /// client whose 201 was lost could otherwise never confirm or cancel it.
    /// </summary>
    private static IResult AlreadyOpen(Guid? openOrderId, PathString path) =>
        TypedResults.Problem(
            detail: "You already have an open checkout for this event. Confirm or cancel it first.",
            statusCode: StatusCodes.Status409Conflict,
            title: "Request cannot be satisfied",
            instance: path,
            extensions: new Dictionary<string, object?>
            {
                ["reason"] = "checkout_already_open",
                ["retriable"] = false,
                ["orderId"] = openOrderId
            });

    private static IResult Conflict(PathString path, string reason, string detail, bool retriable) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status409Conflict,
            title: "Request cannot be satisfied",
            instance: path,
            extensions: new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["retriable"] = retriable
            });

    /// <summary>
    /// The request is wrong on its own terms. Carries a <c>reason</c> like the 409s.
    /// </summary>
    private static IResult Invalid(PathString path, string reason, string detail, int? limit = null)
    {
        var extensions = new Dictionary<string, object?> { ["reason"] = reason };

        if (limit is { } value)
        {
            extensions["limit"] = value;
        }

        return TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid checkout",
            instance: path,
            extensions: extensions);
    }
}
