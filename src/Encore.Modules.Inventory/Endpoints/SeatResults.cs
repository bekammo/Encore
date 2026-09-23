using Encore.Modules.Inventory.Application;
using Microsoft.AspNetCore.Http;

namespace Encore.Modules.Inventory.Endpoints;

/// <summary>
/// Turns a use-case outcome into an HTTP response. The only place in the module
/// that knows a status code.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule, stated once: the status carries the class of failure, and a
/// <c>reason</c> member carries which failure.</b> Everything that is a refusal
/// about the state of the world is <c>409 Conflict</c>; only "no such seat"
/// (404) differs. Splitting refusals across a scatter of codes would make
/// clients branch on status, and the statuses would then have to stay stable
/// forever; a machine-readable <c>reason</c> string is the thing worth
/// promising.
/// </para>
/// <para>
/// There is no 401 or 403 anywhere, because there is no authentication —
/// <c>X-Client-Id</c> is a claimed identity. A 403 would imply an authorisation
/// system that does not exist.
/// </para>
/// <para>
/// Extracted from the endpoints so it can be tested without a host: every
/// switch below is exhaustive with no default arm, so adding an outcome breaks
/// the build rather than silently falling through — which is the entire point
/// of those enums being closed sets.
/// </para>
/// </remarks>
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
                retriable: true),

            _ => throw new ArgumentOutOfRangeException(nameof(result), result.Outcome, "Unmapped hold outcome.")
        };

    /// <summary>Maps the outcome of a sale.</summary>
    public static IResult ForSell(Guid seatId, SellSeatResult result, PathString path) =>
        result.Outcome switch
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
                path, "lost_race", "The seat changed while your request was in flight.", retriable: true),

            _ => throw new ArgumentOutOfRangeException(nameof(result), result.Outcome, "Unmapped sell outcome.")
        };

    /// <summary>Maps the outcome of a release.</summary>
    public static IResult ForRelease(Guid seatId, ReleaseSeatResult result, PathString path) =>
        result.Outcome switch
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
                path, "lost_race", "The seat changed while your request was in flight.", retriable: true),

            _ => throw new ArgumentOutOfRangeException(nameof(result), result.Outcome, "Unmapped release outcome.")
        };

    /// <summary>
    /// "No such seat at this event" — which also covers a seat that exists under
    /// a different event. Deliberately the same answer, so nobody can discover
    /// which seat ids exist by asking about an event they are not looking at.
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

            // Whether trying the identical request again could plausibly work.
            // Under flash-sale load most refusals are ordinary, not faults, and
            // a client needs to know which ones are worth another attempt
            // without hard-coding a list of reason strings.
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
