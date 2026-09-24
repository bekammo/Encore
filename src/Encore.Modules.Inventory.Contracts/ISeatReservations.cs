namespace Encore.Modules.Inventory.Contracts;

/// <summary>
/// Every call is idempotent, and only an empty or repeated list of seat ids throws. Hold and
/// release answer each seat on its own, in request order; a sale is all or none (011). A seat
/// under another event answers <c>SeatNotFound</c>, so ids cannot be probed.
/// </summary>
public interface ISeatReservations
{
    /// <summary>The hold cap is applied in request order.</summary>
    Task<HoldSeatsResponse> HoldAsync(HoldSeatsRequest request, CancellationToken cancellationToken = default);

    Task<ReleaseSeatsResponse> ReleaseAsync(ReleaseSeatsRequest request, CancellationToken cancellationToken = default);

    Task<SellSeatsResponse> SellAsync(SellSeatsRequest request, CancellationToken cancellationToken = default);
}
