namespace Encore.Modules.Inventory.Contracts.Events;

/// <summary>
/// Published when a held seat returns to the available pool, whether the client
/// gave it up or the hold lapsed.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Reason"/> is a string, not the domain enum, and that is
/// deliberate.</b> An enum crossing a wire is an integer, and an integer only means
/// something if both ends agree on the member order — so inserting a member in
/// <c>SeatReleaseReason</c> would silently re-label every row already written. A
/// string cannot drift that way. The closed set is <c>"cancelled"</c> and
/// <c>"expired"</c>; an unrecognised value is a consumer's problem to report rather
/// than to guess at.
/// </para>
/// <para>
/// Keeping the distinction at all is 007's call: "your hold ran out" and "you
/// changed your mind" are different things to tell a customer, and the share of
/// holds that time out is the number that says whether five minutes is the right
/// window. Neither is answerable later if the field was never written.
/// </para>
/// </remarks>
/// <param name="SeatId">The seat that was released.</param>
/// <param name="EventId">The concert the seat belongs to.</param>
/// <param name="ClientId">Whose hold ended.</param>
/// <param name="Reason"><c>"cancelled"</c> or <c>"expired"</c>.</param>
/// <param name="OccurredAt">When the release happened. UTC.</param>
public sealed record SeatReleasedV1(
    Guid SeatId,
    Guid EventId,
    Guid ClientId,
    string Reason,
    DateTime OccurredAt)
{
    /// <summary>The holding client gave the seat up deliberately.</summary>
    public static readonly string Cancelled = "cancelled";

    /// <summary>The hold lapsed, by lazy reclaim or by the sweep.</summary>
    public static readonly string Expired = "expired";
}
