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
/// seat succeeds; selling a seat this client already bought is no refusal. A
/// retried request after a dropped response is therefore safe.
/// </para>
/// <para>
/// <b>The shape survives becoming remote.</b> Nothing here names a type from
/// Inventory's domain or from any persistence or caching library; requests and
/// responses are flat records of primitives, and every call is asynchronous and
/// cancellable. The day Inventory is extracted, this interface is implemented by
/// an HTTP client instead and no consumer changes.
/// </para>
/// <para>
/// <b>Every call takes an order's seats together</b>, in one round trip and one
/// transaction. Holding and releasing still answer each seat on its own; selling
/// is all or none, because a sold seat cannot be taken back (076).
/// </para>
/// </remarks>
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
