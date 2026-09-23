namespace Encore.Modules.Inventory.Application;

/// <summary>
/// The one precondition every seat batch shares.
/// </summary>
internal static class SeatBatch
{
    /// <summary>
    /// Refuses an empty batch or one naming a seat twice.
    /// </summary>
    /// <remarks>
    /// A caller bug rather than a refusal, so it throws. A repeated seat would be
    /// counted twice against the hold cap, and Orders already refuses one before
    /// it gets here (025).
    /// </remarks>
    public static void EnsureValid(IReadOnlyList<Guid> seatIds)
    {
        ArgumentNullException.ThrowIfNull(seatIds);

        if (seatIds.Count is 0)
        {
            throw new ArgumentException("A batch must name at least one seat.", nameof(seatIds));
        }

        if (seatIds.Distinct().Count() != seatIds.Count)
        {
            throw new ArgumentException("A batch must not name a seat twice.", nameof(seatIds));
        }
    }
}
