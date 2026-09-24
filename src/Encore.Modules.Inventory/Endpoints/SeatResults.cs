using Encore.Modules.Inventory.Application;
using Encore.Modules.Inventory.Contracts;
using Microsoft.AspNetCore.Http;

namespace Encore.Modules.Inventory.Endpoints;

internal static class SeatResults
{
    public static IResult ForHold(Guid seatId, HoldSeatResult result, PathString path) =>
        result.Outcome switch
        {
            HoldSeatOutcome.Held =>
                TypedResults.Ok(new HeldSeatResponse(seatId, result.HoldExpiresAt!.Value)),

            HoldSeatOutcome.SeatNotFound => NotFound(path),

            HoldSeatOutcome.AlreadyHeld => Conflict(
                path, "already_held", "Somebody else is holding this seat.", retriable: true),

            HoldSeatOutcome.AlreadySold => Conflict(
                path, "already_sold", "This seat has been sold.", retriable: false),

            HoldSeatOutcome.HoldCapReached => Conflict(
                path,
                "hold_cap_reached",
                $"You may hold at most {SeatReservationLimits.MaxHoldsPerClientPerEvent} seats "
                + "at this event at once. Release one or complete checkout first.",
                retriable: true,
                limit: SeatReservationLimits.MaxHoldsPerClientPerEvent),

            HoldSeatOutcome.LostRace => Conflict(
                path, "lost_race", "The seat changed while your request was in flight.", retriable: true),

            HoldSeatOutcome.ConcurrentRequestInFlight => Conflict(
                path,
                "concurrent_request_in_flight",
                "Another hold request from you for this event is still in flight.",
                retriable: true)
        };

    public static IResult ForSell(Guid seatId, SellSeatOutcome outcome, PathString path) =>
        outcome switch
        {
            SellSeatOutcome.Sold => TypedResults.Ok(new SeatActionResponse(seatId, "sold")),

            SellSeatOutcome.SeatNotFound => NotFound(path),

            SellSeatOutcome.AlreadySold => Conflict(
                path, "already_sold", "Somebody else has bought this seat.", retriable: false),

            SellSeatOutcome.NotTheHolder => Conflict(
                path, "not_the_holder", "You are not holding this seat.", retriable: false),

            SellSeatOutcome.HoldExpired => Conflict(
                path, "hold_expired", "Your hold on this seat lapsed before checkout completed.", retriable: true),

            SellSeatOutcome.NoActiveHold => Conflict(
                path, "no_active_hold", "This seat is not held. Hold it before buying it.", retriable: true),

            SellSeatOutcome.LostRace => Conflict(
                path, "lost_race", "The seat changed while your request was in flight.", retriable: true)
        };

    public static IResult ForRelease(Guid seatId, ReleaseSeatOutcome outcome, PathString path) =>
        outcome switch
        {
            ReleaseSeatOutcome.Released => TypedResults.Ok(new SeatActionResponse(seatId, "available")),

            ReleaseSeatOutcome.SeatNotFound => NotFound(path),

            ReleaseSeatOutcome.AlreadySold => Conflict(
                path, "already_sold", "This seat has been sold and cannot be released.", retriable: false),

            ReleaseSeatOutcome.SoldToYou => Conflict(
                path, "sold_to_you", "You have bought this seat, and a sale cannot be released.", retriable: false),

            ReleaseSeatOutcome.NotTheHolder => Conflict(
                path, "not_the_holder", "You are not holding this seat.", retriable: false),

            ReleaseSeatOutcome.LostRace => Conflict(
                path, "lost_race", "The seat changed while your request was in flight.", retriable: true)
        };

    private static IResult NotFound(PathString path) =>
        TypedResults.Problem(
            detail: "No such seat at this event.",
            statusCode: StatusCodes.Status404NotFound,
            title: "Seat not found",
            instance: path,
            extensions: new Dictionary<string, object?> { ["reason"] = "seat_not_found" });

    private static IResult Conflict(
        PathString path,
        string reason,
        string detail,
        bool retriable,
        int? limit = null)
    {
        var extensions = new Dictionary<string, object?>
        {
            ["reason"] = reason,

            // Whether trying again could succeed: later, or after a new hold.
            ["retriable"] = retriable
        };

        if (limit is { } value)
        {
            extensions["limit"] = value;
        }

        return TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status409Conflict,
            title: "Seat unavailable",
            instance: path,
            extensions: extensions);
    }
}
