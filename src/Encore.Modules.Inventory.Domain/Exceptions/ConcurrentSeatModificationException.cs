namespace Encore.Modules.Inventory.Domain.Exceptions;

/// <summary>
/// Adapters translate their storage error into this, so callers never depend on the
/// persistence library (002).
/// </summary>
public sealed class ConcurrentSeatModificationException : Exception
{
    public ConcurrentSeatModificationException(Guid seatId)
        : base($"Seat {seatId} was modified concurrently and the write was rejected.")
        => SeatId = seatId;

    public ConcurrentSeatModificationException(Guid seatId, Exception innerException)
        : base($"Seat {seatId} was modified concurrently and the write was rejected.", innerException)
        => SeatId = seatId;

    public Guid SeatId { get; }
}
