using Encore.Modules.Inventory.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Encore.Modules.Inventory.Endpoints;

/// <summary>
/// Inventory's HTTP surface: use-case routes, not CRUD.
/// </summary>
/// <remarks>
/// <para>
/// <b>Actions, not a hold resource.</b> The routes are
/// <c>.../hold</c>, <c>.../release</c> and <c>.../purchase</c> rather than a
/// <c>/holds</c> collection, because a hold is not an entity — it is two fields
/// on the seat row (<c>DECISIONS.md</c> 005 and 007) and has no identity of its
/// own. Giving it a URI would contradict the aggregate's central design choice
/// for the sake of looking more RESTful.
/// </para>
/// <para>
/// <b>Every action is idempotent</b>, which is what makes retrying a POST safe
/// here: re-holding a seat you hold, re-releasing one you have released and
/// re-buying one you have bought are all no-op successes. That is a deliberate
/// property of the aggregate, not an accident of the endpoints.
/// </para>
/// <para>
/// Each handler is a single call plus a mapping. There is no branching in this
/// file: <see cref="SeatResults"/> owns the outcome-to-status decisions, so
/// they can be tested without a host.
/// </para>
/// </remarks>
public static class SeatEndpoints
{
    /// <summary>Maps the seat routes under <c>/events/{eventId}</c>.</summary>
    public static IEndpointRouteBuilder MapSeatEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var events = endpoints.MapGroup("/events/{eventId:guid}");

        // Operator-facing, and the only route here with no client identity:
        // creating a seat map is not something a customer does.
        events.MapPost("/seats", CreateSeatMapAsync)
            .WithName("CreateSeatMap")
            .WithSummary("Creates an event's seats and returns their ids.");

        // Everything below acts on behalf of a client, so the filter goes on the
        // group rather than on each route — an endpoint added later cannot
        // forget it.
        var seat = events.MapGroup("/seats/{seatId:guid}")
            .AddEndpointFilter<ClientIdEndpointFilter>();

        seat.MapPost("/hold", HoldAsync)
            .WithName("HoldSeat")
            .WithSummary("Holds a seat for the calling client for five minutes.");

        seat.MapPost("/release", ReleaseAsync)
            .WithName("ReleaseSeat")
            .WithSummary("Gives a held seat back.");

        seat.MapPost("/purchase", PurchaseAsync)
            .WithName("PurchaseSeat")
            .WithSummary("Converts the calling client's live hold into a sale.");

        return endpoints;
    }

    private static async Task<IResult> CreateSeatMapAsync(
        Guid eventId,
        CreateSeatMapRequest request,
        CreateSeatMapCommandHandler handler,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        // Refused here so it stays a 400. Seat.Create rejects an empty event id
        // too (DECISIONS 038), but that throw is the belt behind this brace: a
        // route id is a request the caller got wrong, and letting the aggregate
        // answer it would turn a malformed request into a 500.
        if (eventId == Guid.Empty)
        {
            return TypedResults.Problem(
                detail: "An event id must not be empty.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid event id",
                instance: context.Request.Path);
        }

        if (request.Count < 1 || request.Count > CreateSeatMapCommandHandler.MaxSeatsPerRequest)
        {
            return TypedResults.Problem(
                detail: $"Count must be between 1 and {CreateSeatMapCommandHandler.MaxSeatsPerRequest}.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid seat count",
                instance: context.Request.Path);
        }

        var result = await handler
            .HandleAsync(new CreateSeatMapCommand(eventId, request.Count), cancellationToken)
            .ConfigureAwait(false);

        // 201 with no Location: there is no GET for a seat map, and a Location
        // header pointing at nothing is worse than none at all.
        return TypedResults.Created(
            (string?)null,
            new CreateSeatMapResponse(eventId, result.SeatIds));
    }

    private static async Task<IResult> HoldAsync(
        Guid eventId,
        Guid seatId,
        HoldSeatCommandHandler handler,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var command = new HoldSeatCommand(eventId, seatId, ClientIdEndpointFilter.ClientId(context));

        var result = await handler.HandleAsync(command, cancellationToken).ConfigureAwait(false);

        return SeatResults.ForHold(seatId, result, context.Request.Path);
    }

    private static async Task<IResult> ReleaseAsync(
        Guid eventId,
        Guid seatId,
        ReleaseSeatCommandHandler handler,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var command = new ReleaseSeatCommand(eventId, seatId, ClientIdEndpointFilter.ClientId(context));

        var result = await handler.HandleAsync(command, cancellationToken).ConfigureAwait(false);

        return SeatResults.ForRelease(seatId, result, context.Request.Path);
    }

    private static async Task<IResult> PurchaseAsync(
        Guid eventId,
        Guid seatId,
        SellSeatCommandHandler handler,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var command = new SellSeatCommand(eventId, seatId, ClientIdEndpointFilter.ClientId(context));

        var result = await handler.HandleAsync(command, cancellationToken).ConfigureAwait(false);

        return SeatResults.ForSell(seatId, result, context.Request.Path);
    }
}
