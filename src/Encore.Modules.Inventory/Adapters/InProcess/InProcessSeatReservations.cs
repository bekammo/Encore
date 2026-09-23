using Encore.Modules.Inventory.Adapters.Telemetry;
using Encore.Modules.Inventory.Application;
using Encore.Modules.Inventory.Contracts;

namespace Encore.Modules.Inventory.Adapters.InProcess;

/// <summary>
/// Serves <see cref="ISeatReservations"/> in process by calling the use cases directly,
/// translating their outcomes into the public contract so internal enums never become
/// part of it.
/// </summary>
internal sealed class InProcessSeatReservations(
    HoldSeatCommandHandler holdSeat,
    ReleaseSeatCommandHandler releaseSeat,
    SellSeatCommandHandler sellSeat) : ISeatReservations
{
    private readonly HoldSeatCommandHandler _holdSeat = holdSeat;
    private readonly ReleaseSeatCommandHandler _releaseSeat = releaseSeat;
    private readonly SellSeatCommandHandler _sellSeat = sellSeat;

    /// <inheritdoc />
    public async Task<HoldSeatsResponse> HoldAsync(
        HoldSeatsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var results = await _holdSeat
            .HandleAsync(
                new HoldSeatsCommand(request.EventId, request.SeatIds, request.ClientId),
                cancellationToken)
            .ConfigureAwait(false);

        foreach (var result in results)
        {
            InventoryTelemetry.RecordSeat("hold", result.Outcome);
        }

        return new HoldSeatsResponse(
            [.. request.SeatIds.Zip(results, (seatId, result) => ToResponse(seatId, result))]);
    }

    /// <inheritdoc />
    public async Task<ReleaseSeatsResponse> ReleaseAsync(
        ReleaseSeatsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var results = await _releaseSeat
            .HandleAsync(
                new ReleaseSeatsCommand(request.EventId, request.SeatIds, request.ClientId),
                cancellationToken)
            .ConfigureAwait(false);

        foreach (var result in results)
        {
            InventoryTelemetry.RecordSeat("release", result.Outcome);
        }

        return new ReleaseSeatsResponse(
            [.. request.SeatIds.Zip(results, (seatId, result) => ToResponse(seatId, result))]);
    }

    /// <inheritdoc />
    public async Task<SellSeatsResponse> SellAsync(
        SellSeatsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _sellSeat
            .HandleAsync(
                new SellSeatsCommand(request.EventId, request.SeatIds, request.ClientId),
                cancellationToken)
            .ConfigureAwait(false);

        // All or none: every seat sold, or only the refusals are worth counting.
        if (result.AllSold)
        {
            InventoryTelemetry.SeatOutcomes.Add(
                request.SeatIds.Count,
                new KeyValuePair<string, object?>("action", "sell"),
                new KeyValuePair<string, object?>("outcome", nameof(SellSeatOutcome.Sold)));
        }

        foreach (var refusal in result.Refusals)
        {
            InventoryTelemetry.RecordSeat("sell", refusal.Outcome);
        }

        return new SellSeatsResponse(
            [.. result.Refusals.Select(refusal => ToResponse(refusal.SeatId, refusal.Outcome))]);
    }

    private static HoldSeatResponse ToResponse(Guid seatId, HoldSeatResult result) => result.Outcome switch
    {
        HoldSeatOutcome.Held =>
            new HoldSeatResponse(seatId, HoldSeatStatus.Held, result.HoldExpiresAt!.Value),

        HoldSeatOutcome.AlreadyHeld => new HoldSeatResponse(seatId, HoldSeatStatus.AlreadyHeld),
        HoldSeatOutcome.AlreadySold => new HoldSeatResponse(seatId, HoldSeatStatus.AlreadySold),
        HoldSeatOutcome.SeatNotFound => new HoldSeatResponse(seatId, HoldSeatStatus.SeatNotFound),
        HoldSeatOutcome.LostRace => new HoldSeatResponse(seatId, HoldSeatStatus.LostRace),
        HoldSeatOutcome.HoldCapReached => new HoldSeatResponse(seatId, HoldSeatStatus.HoldCapReached),
        HoldSeatOutcome.ConcurrentRequestInFlight =>
            new HoldSeatResponse(seatId, HoldSeatStatus.ConcurrentRequestInFlight),

        _ => throw new ArgumentOutOfRangeException(
            nameof(result), result.Outcome, "Unmapped hold outcome.")
    };

    private static ReleaseSeatResponse ToResponse(Guid seatId, ReleaseSeatResult result) => result.Outcome switch
    {
        ReleaseSeatOutcome.Released => new ReleaseSeatResponse(seatId, ReleaseSeatStatus.Released),
        ReleaseSeatOutcome.AlreadySold => new ReleaseSeatResponse(seatId, ReleaseSeatStatus.AlreadySold),
        ReleaseSeatOutcome.NotTheHolder => new ReleaseSeatResponse(seatId, ReleaseSeatStatus.NotTheHolder),
        ReleaseSeatOutcome.SeatNotFound => new ReleaseSeatResponse(seatId, ReleaseSeatStatus.SeatNotFound),
        ReleaseSeatOutcome.LostRace => new ReleaseSeatResponse(seatId, ReleaseSeatStatus.LostRace),
        ReleaseSeatOutcome.SoldToYou => new ReleaseSeatResponse(seatId, ReleaseSeatStatus.SoldToYou),

        _ => throw new ArgumentOutOfRangeException(
            nameof(result), result.Outcome, "Unmapped release outcome.")
    };

    private static SellSeatResponse ToResponse(Guid seatId, SellSeatOutcome outcome) => outcome switch
    {
        SellSeatOutcome.Sold => new SellSeatResponse(seatId, SellSeatStatus.Sold),
        SellSeatOutcome.AlreadySold => new SellSeatResponse(seatId, SellSeatStatus.AlreadySold),
        SellSeatOutcome.NotTheHolder => new SellSeatResponse(seatId, SellSeatStatus.NotTheHolder),
        SellSeatOutcome.HoldExpired => new SellSeatResponse(seatId, SellSeatStatus.HoldExpired),
        SellSeatOutcome.NoActiveHold => new SellSeatResponse(seatId, SellSeatStatus.NoActiveHold),
        SellSeatOutcome.SeatNotFound => new SellSeatResponse(seatId, SellSeatStatus.SeatNotFound),
        SellSeatOutcome.LostRace => new SellSeatResponse(seatId, SellSeatStatus.LostRace),

        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome, "Unmapped sell outcome.")
    };
}
