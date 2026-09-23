namespace Encore.Modules.Inventory.Domain.Exceptions;

/// <summary>
/// A seat changed after it was loaded, so the write was rejected: someone else won
/// the race. Adapters translate their storage-specific error into this, so callers
/// never depend on the persistence library.
/// </summary>
public sealed class ConcurrentSeatModificationException : Exception
{
    public ConcurrentSeatModificationException(Guid seatId)
        : base($"Seat {seatId} was modified concurrently and the write was rejected.")
        => SeatId = seatId;

    /// <summary>Creates the exception, preserving the adapter-level cause.</summary>
    public ConcurrentSeatModificationException(Guid seatId, Exception innerException)
        : base($"Seat {seatId} was modified concurrently and the write was rejected.", innerException)
        => SeatId = seatId;

    public Guid SeatId { get; }
}
