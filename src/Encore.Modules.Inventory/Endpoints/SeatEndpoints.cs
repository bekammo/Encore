using Encore.Modules.Inventory.Adapters.Telemetry;
using Encore.Modules.Inventory.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Encore.Modules.Inventory.Endpoints;

/// <summary>
/// Actions rather than a <c>/holds</c> resource, because a hold is two fields on the seat, not
/// an entity. The actions are idempotent; creating a seat map is not, and each call adds seats.
/// </summary>
/// <remarks>
/// The actions go around the order: no price, no payment, no on-sale check. They stay open
/// until Identity can restrict them, as every route does (008, 012).
/// </remarks>
public static class SeatEndpoints
{
    public static IEndpointRouteBuilder MapSeatEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var events = endpoints.MapGroup("/events/{eventId:guid}");

        // Operator-facing, so no client identity.
        events.MapPost("/seats", CreateSeatMapAsync);

        // On the group, so a route added later cannot forget the client filter.
        var seat = events.MapGroup("/seats/{seatId:guid}")
            .AddEndpointFilter<ClientIdEndpointFilter>();

        seat.MapPost("/hold", HoldAsync);
        seat.MapPost("/release", ReleaseAsync);
        seat.MapPost("/purchase", PurchaseAsync);

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

        var seatIds = await handler
            .HandleAsync(new CreateSeatMapCommand(eventId, request.Count), cancellationToken)
            .ConfigureAwait(false);

        // No Location header: there is no GET for a seat map.
        return TypedResults.Created(
            (string?)null,
            new CreateSeatMapResponse(eventId, seatIds));
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
        InventoryTelemetry.RecordSeat("hold", result.Outcome);

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

        var outcome = await handler.HandleAsync(command, cancellationToken).ConfigureAwait(false);
        InventoryTelemetry.RecordSeat("release", outcome);

        return SeatResults.ForRelease(seatId, outcome, context.Request.Path);
    }

    private static async Task<IResult> PurchaseAsync(
        Guid eventId,
        Guid seatId,
        SellSeatCommandHandler handler,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var command = new SellSeatCommand(eventId, seatId, ClientIdEndpointFilter.ClientId(context));

        var outcome = await handler.HandleAsync(command, cancellationToken).ConfigureAwait(false);
        InventoryTelemetry.RecordSeat("sell", outcome);

        return SeatResults.ForSell(seatId, outcome, context.Request.Path);
    }
}
