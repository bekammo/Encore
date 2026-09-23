using Encore.Modules.Inventory.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Encore.Modules.Inventory.Endpoints;

/// <summary>
/// Inventory's HTTP surface. Actions (<c>hold</c>, <c>release</c>, <c>purchase</c>) rather
/// than a <c>/holds</c> resource, because a hold is two fields on the seat, not an entity.
/// Every action is idempotent, so retrying a POST is safe.
/// </summary>
public static class SeatEndpoints
{
    /// <summary>Maps the seat routes under <c>/events/{eventId}</c>.</summary>
    public static IEndpointRouteBuilder MapSeatEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var events = endpoints.MapGroup("/events/{eventId:guid}");

        // Operator-facing, so no client identity.
        events.MapPost("/seats", CreateSeatMapAsync)
            .WithName("CreateSeatMap")
            .WithSummary("Creates an event's seats and returns their ids.");

        // On the group, so a route added later cannot forget the client filter.
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
        // Refused here so it is a 400, not a 500 from Seat.Create.
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

        // No Location header: there is no GET for a seat map.
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
