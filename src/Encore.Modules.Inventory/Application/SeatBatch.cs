namespace Encore.Modules.Inventory.Application;

internal static class SeatBatch
{
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
