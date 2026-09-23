namespace Encore.Modules.Inventory.Contracts;

/// <summary>
/// Inventory's seat reservations as other modules see them. Refusals are returned, not
/// thrown; every operation is idempotent; and nothing here names a domain or library type,
/// so it can be served over HTTP later without changing callers. Each call takes an
/// order's seats together; selling is all or none.
/// </summary>
public interface ISeatReservations
{
    /// <summary>Holds seats for the requesting client, for a fixed window Inventory owns.</summary>
    Task<HoldSeatsResponse> HoldAsync(HoldSeatsRequest request, CancellationToken cancellationToken = default);

    /// <summary>Gives held seats back. Only the holding client may release.</summary>
    Task<ReleaseSeatsResponse> ReleaseAsync(ReleaseSeatsRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Converts this client's live holds into sales — all of them, or none.
    /// Requires an unexpired hold on every seat.
    /// </summary>
    Task<SellSeatsResponse> SellAsync(SellSeatsRequest request, CancellationToken cancellationToken = default);
}
