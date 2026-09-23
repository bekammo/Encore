using Encore.Modules.Inventory.Application;
using Encore.Modules.Inventory.Contracts;

namespace Encore.Modules.Inventory.Adapters.InProcess;

/// <summary>
/// Serves <see cref="ISeatReservations"/> by calling the module's use cases
/// directly, in the caller's process and transaction-less as they already are.
/// </summary>
/// <remarks>
/// <para>
/// This is a driving adapter, and the in-process twin of
/// <c>SeatEndpoints</c> — a second way in to the same four use cases, for
/// callers that are modules rather than HTTP clients. Both translate the
/// Application layer's outcome enums into a vocabulary their own audience
/// owns, and neither re-decides anything: no rule about when a seat may change
/// hands exists anywhere but inside <c>Seat</c>.
/// </para>
/// <para>
/// <b>Why translate the enums at all, rather than publish the Application
/// ones.</b> Publishing them would make every internal outcome a public
/// contract, so renaming <c>HoldSeatOutcome.LostRace</c> would break another
/// module — and once Inventory is extracted the wire format would be pinned to
/// an internal type. The cost is one mapping per operation, which is the same
/// duty <c>SeatResults</c> performs for HTTP and for the same reason.
/// </para>
/// <para>
/// Every switch maps each named member explicitly, so adding an outcome to an
/// Application enum fails the build here rather than silently reaching the
/// throwing arm at run time.
/// </para>
/// </remarks>
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

        return new SellSeatsResponse(
            [.. result.Refusals.Select(refusal => ToResponse(refusal.SeatId, refusal.Outcome))]);
    }

    private static HoldSeatResponse ToResponse(Guid seatId, HoldSeatResult result) => result.Outcome switch
    {
        // The expiry is only ever present on success, which is exactly the
        // promise HoldSeatResponse makes, so the bang is safe here and nowhere
        // else.
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
