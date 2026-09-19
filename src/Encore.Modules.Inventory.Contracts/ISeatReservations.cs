namespace Encore.Modules.Inventory.Contracts;

/// <summary>
/// Inventory's seat reservation capability, as other modules see it.
/// </summary>
/// <remarks>
/// <para>
/// Three properties are promised here, and callers may rely on all three.
/// </para>
/// <para>
/// <b>Refusals are return values, not exceptions.</b> Losing a seat to somebody
/// else is the single most common outcome during a flash sale, not an error, so
/// every response carries a closed status enum the caller switches over. An
/// exception from any of these methods means something genuinely broke.
/// </para>
/// <para>
/// <b>Every operation is idempotent.</b> Holding a seat this client already
/// holds succeeds without moving the expiry; releasing an already-available
/// seat succeeds; selling a seat this client already bought reports
/// <see cref="SellSeatStatus.Sold"/>. A retried request after a dropped response
/// is therefore safe.
/// </para>
/// <para>
/// <b>The shape survives becoming remote.</b> Nothing here names a type from
/// Inventory's domain or from any persistence or caching library; requests and
/// responses are flat records of primitives, and every call is asynchronous and
/// cancellable. The day Inventory is extracted, this interface is implemented by
/// an HTTP client instead and no consumer changes.
/// </para>
/// </remarks>
public interface ISeatReservations
{
    /// <summary>Holds a seat for the requesting client, for a fixed window Inventory owns.</summary>
    Task<HoldSeatResponse> HoldAsync(HoldSeatRequest request, CancellationToken cancellationToken = default);

    /// <summary>Gives a held seat back. Only the holding client may release.</summary>
    Task<ReleaseSeatResponse> ReleaseAsync(ReleaseSeatRequest request, CancellationToken cancellationToken = default);

    /// <summary>Converts this client's live hold into a sale. Requires an unexpired hold.</summary>
    Task<SellSeatResponse> SellAsync(SellSeatRequest request, CancellationToken cancellationToken = default);
}
