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
    public async Task<HoldSeatResponse> HoldAsync(
        HoldSeatRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _holdSeat
            .HandleAsync(
                new HoldSeatCommand(request.EventId, request.SeatId, request.ClientId),
                cancellationToken)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            // The expiry is only ever present on success, which is exactly the
            // promise HoldSeatResponse makes, so the bang is safe here and
            // nowhere else.
            HoldSeatOutcome.Held =>
                new HoldSeatResponse(HoldSeatStatus.Held, result.HoldExpiresAt!.Value),

            HoldSeatOutcome.AlreadyHeld => new HoldSeatResponse(HoldSeatStatus.AlreadyHeld),
            HoldSeatOutcome.AlreadySold => new HoldSeatResponse(HoldSeatStatus.AlreadySold),
            HoldSeatOutcome.SeatNotFound => new HoldSeatResponse(HoldSeatStatus.SeatNotFound),
            HoldSeatOutcome.LostRace => new HoldSeatResponse(HoldSeatStatus.LostRace),
            HoldSeatOutcome.HoldCapReached => new HoldSeatResponse(HoldSeatStatus.HoldCapReached),
            HoldSeatOutcome.ConcurrentRequestInFlight =>
                new HoldSeatResponse(HoldSeatStatus.ConcurrentRequestInFlight),

            _ => throw new ArgumentOutOfRangeException(
                nameof(request), result.Outcome, "Unmapped hold outcome.")
        };
    }

    /// <inheritdoc />
    public async Task<ReleaseSeatResponse> ReleaseAsync(
        ReleaseSeatRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _releaseSeat
            .HandleAsync(
                new ReleaseSeatCommand(request.EventId, request.SeatId, request.ClientId),
                cancellationToken)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            ReleaseSeatOutcome.Released => new ReleaseSeatResponse(ReleaseSeatStatus.Released),
            ReleaseSeatOutcome.AlreadySold => new ReleaseSeatResponse(ReleaseSeatStatus.AlreadySold),
            ReleaseSeatOutcome.NotTheHolder => new ReleaseSeatResponse(ReleaseSeatStatus.NotTheHolder),
            ReleaseSeatOutcome.SeatNotFound => new ReleaseSeatResponse(ReleaseSeatStatus.SeatNotFound),
            ReleaseSeatOutcome.LostRace => new ReleaseSeatResponse(ReleaseSeatStatus.LostRace),

            _ => throw new ArgumentOutOfRangeException(
                nameof(request), result.Outcome, "Unmapped release outcome.")
        };
    }

    /// <inheritdoc />
    public async Task<SellSeatResponse> SellAsync(
        SellSeatRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _sellSeat
            .HandleAsync(
                new SellSeatCommand(request.EventId, request.SeatId, request.ClientId),
                cancellationToken)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            SellSeatOutcome.Sold => new SellSeatResponse(SellSeatStatus.Sold),
            SellSeatOutcome.AlreadySold => new SellSeatResponse(SellSeatStatus.AlreadySold),
            SellSeatOutcome.NotTheHolder => new SellSeatResponse(SellSeatStatus.NotTheHolder),
            SellSeatOutcome.HoldExpired => new SellSeatResponse(SellSeatStatus.HoldExpired),
            SellSeatOutcome.NoActiveHold => new SellSeatResponse(SellSeatStatus.NoActiveHold),
            SellSeatOutcome.SeatNotFound => new SellSeatResponse(SellSeatStatus.SeatNotFound),
            SellSeatOutcome.LostRace => new SellSeatResponse(SellSeatStatus.LostRace),

            _ => throw new ArgumentOutOfRangeException(
                nameof(request), result.Outcome, "Unmapped sell outcome.")
        };
    }
}
