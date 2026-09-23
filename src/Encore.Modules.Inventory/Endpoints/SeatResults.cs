using Encore.Modules.Inventory.Application;
using Microsoft.AspNetCore.Http;

namespace Encore.Modules.Inventory.Endpoints;

/// <summary>
/// Maps use-case outcomes to HTTP responses. Refusals about the state of the world are
/// 409 with a machine-readable <c>reason</c>; only a missing seat is 404. Every switch is
/// exhaustive, so a new outcome breaks the build.
/// </summary>
internal static class SeatResults
{
    /// <summary>Maps the outcome of a hold.</summary>
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
                $"You may hold at most {HoldSeatCommandHandler.MaxHoldsPerClientPerEvent} seats "
                + "at this event at once. Release one or complete checkout first.",
                retriable: true,
                limit: HoldSeatCommandHandler.MaxHoldsPerClientPerEvent),

            HoldSeatOutcome.LostRace => Conflict(
                path, "lost_race", "The seat changed while your request was in flight.", retriable: true),

            HoldSeatOutcome.ConcurrentRequestInFlight => Conflict(
                path,
                "concurrent_request_in_flight",
                "Another hold request from you for this event is still in flight.",
                retriable: true)
        };

    /// <summary>Maps the outcome of a sale.</summary>
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

    /// <summary>Maps the outcome of a release.</summary>
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

    /// <summary>
    /// A missing seat and a seat under another event get the same answer, so ids cannot be probed.
    /// </summary>
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

            // Whether trying again could succeed: later, or after a new hold or a different card.
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
