using Encore.Modules.Inventory.Contracts;
using Encore.Modules.Orders.Models;
using Microsoft.AspNetCore.Http;

namespace Encore.Modules.Orders.Endpoints;

/// <summary>
/// Turns a service outcome into an HTTP response. The only place in this module
/// that knows a status code.
/// </summary>
/// <remarks>
/// <para>
/// This <i>is</i> the twin of <c>SeatResults</c>, where <c>CatalogResults</c>
/// deliberately was not. Catalog had no outcome enums to switch over; this
/// module has three, and something has to decide exhaustively what each case
/// deserves. Every switch below has no default arm, so adding an outcome breaks
/// the build — which is the whole reason those enums are closed sets.
/// </para>
/// <para>
/// The status rule is 014's, unchanged: the code carries the class of failure
/// and a <c>reason</c> member carries which one. Refusals about the state of the
/// world are 409; only an order that is not there is 404; a request that is
/// wrong on its own terms is 400.
/// </para>
/// <para>
/// <b>The 400/409 split is the interesting line here.</b> Asking for more seats
/// than the published cap is a 400, because the request contradicts a number the
/// client could have read before sending it. Being told
/// <c>hold_cap_reached</c> by Inventory is a 409, because that is a fact about
/// the world — seats held in another tab count. Both can happen on one checkout,
/// and they are not the same mistake.
/// </para>
/// </remarks>
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

            // The one refusal here that stops being true on its own, so the flag
            // is the honest answer to "is another attempt worth making".
            CheckoutOutcome.NotOnSale => Conflict(
                path, "not_on_sale", "Tickets for this event are not on sale yet.", retriable: true),

            CheckoutOutcome.CheckoutAlreadyOpen => Conflict(
                path,
                "checkout_already_open",
                "You already have an open checkout for this event. Complete or cancel it first.",
                retriable: false),

            CheckoutOutcome.SeatsUnavailable => SeatsUnavailable(result.Refusals!, path),

            _ => throw new ArgumentOutOfRangeException(
                nameof(result), result.Outcome, "Unmapped checkout outcome.")
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
    /// A confirm ran and the order reached an ending. Which ending is on the
    /// order, because that is where the fact lives.
    /// </summary>
    private static IResult ForEnding(Order order, PathString path) =>
        order.Status switch
        {
            OrderStatus.Confirmed => TypedResults.Ok(OrderResponse.From(order)),

            // 200, not a conflict. The customer has every seat they asked for and
            // nothing has failed from where they are standing — the only thing
            // outstanding is ours to finish, and the status field says so for any
            // client that cares to look.
            OrderStatus.AwaitingCapture => TypedResults.Ok(OrderResponse.From(order)),

            // Not retriable: the holds are gone, and this module is not the thing
            // that could get them back. A client that still wants the seats starts
            // a new checkout, which is a different request.
            OrderStatus.Expired => Conflict(
                path,
                "holds_expired",
                "The holds on this order lapsed before it was confirmed.",
                retriable: false),

            OrderStatus.Failed => Conflict(
                path,
                "order_failed",
                // Since 076 no order is partly sold, so this no longer says so.
                "This order could not be completed and needs to be looked at.",
                retriable: false),

            // Confirm never leaves an order Pending or Cancelled, so reaching
            // here means the service and this mapping disagree about the state
            // machine, which is a bug rather than a response.
            OrderStatus.Pending or OrderStatus.Cancelled => throw new ArgumentOutOfRangeException(
                nameof(order), order.Status, "A completed action left the order in a non-terminal status."),

            _ => throw new ArgumentOutOfRangeException(
                nameof(order), order.Status, "Unmapped order status.")
        };

    /// <summary>The shared refusals of confirm and cancel.</summary>
    private static IResult ForFailedAction(OrderActionResult result, PathString path) =>
        result.Outcome switch
        {
            OrderActionOutcome.OrderNotFound => NotFound(path),

            // The status is rendered through OrderResponse rather than formatted
            // here, so a client reads one spelling of a status whether it arrives
            // in a body or in a sentence. It matters now that one of them is two
            // words.
            OrderActionOutcome.NotPending => Conflict(
                path,
                "order_not_pending",
                $"This order is {OrderResponse.From(result.Order!).Status} and cannot be changed.",
                retriable: false),

            OrderActionOutcome.LostRace => Conflict(
                path,
                "lost_race",
                "The order changed while your request was in flight.",
                retriable: true),

            // Retriable, and this is the one place in the module where that flag
            // means "try again with something different" rather than "try the
            // identical request again". The order is untouched and its holds are
            // still live, which is the whole reason a decline does not end it.
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
                nameof(result), result.Outcome, "Completed is not a failed action."),

            _ => throw new ArgumentOutOfRangeException(
                nameof(result), result.Outcome, "Unmapped order action outcome.")
        };

    /// <summary>
    /// One or more seats could not be held, so nothing was written.
    /// </summary>
    /// <remarks>
    /// The top-level <c>retriable</c> answers a precise question: could sending
    /// this identical request again succeed? Only if every refusal is itself
    /// retriable — one sold seat makes the whole list a lost cause, however many
    /// of the others merely lost a race. The per-seat flags are what a client
    /// uses to decide which seats to swap out.
    /// </remarks>
    private static IResult SeatsUnavailable(IReadOnlyList<SeatRefusal> refusals, PathString path)
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
    /// Inventory's status as a reason string, using Inventory's own vocabulary
    /// so a client sees one set of words whether it called that module directly
    /// or reached it through a checkout.
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
                nameof(status), status, "A held seat is not a refusal."),

            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped hold status.")
        };

    private static bool IsRetriable(HoldSeatStatus status) =>
        status switch
        {
            // Somebody else holds it, but a hold lapses; the cap frees up as this
            // client's own holds lapse or complete; a lost race and an in-flight
            // request are the system refusing rather than the seat.
            HoldSeatStatus.AlreadyHeld => true,
            HoldSeatStatus.LostRace => true,
            HoldSeatStatus.HoldCapReached => true,
            HoldSeatStatus.ConcurrentRequestInFlight => true,

            // Sold is terminal, and a seat that does not exist will not start to.
            HoldSeatStatus.AlreadySold => false,
            HoldSeatStatus.SeatNotFound => false,

            HoldSeatStatus.Held => throw new ArgumentOutOfRangeException(
                nameof(status), status, "A held seat is not a refusal."),

            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped hold status.")
        };

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
    /// The request is wrong on its own terms. Carries a <c>reason</c> like the
    /// others: 018 settled that the status names the class of failure and the
    /// reason names which one, and that is as useful at 400 as at 409.
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
