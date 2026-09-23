namespace Encore.Modules.Inventory.Application;

/// <summary>
/// The one precondition every seat batch shares.
/// </summary>
internal static class SeatBatch
{
    /// <summary>
    /// Refuses an empty batch or one naming a seat twice.
    /// </summary>
    /// <remarks>A caller bug, so it throws; a repeated seat would count twice against the cap.</remarks>
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
