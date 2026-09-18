namespace Encore.Modules.Inventory.Domain.Exceptions;

/// <summary>
/// Thrown when a seat could not be written because it changed underneath the
/// caller since it was loaded — someone else won the race for it.
/// </summary>
/// <remarks>
/// This is the module's own vocabulary for losing an optimistic-concurrency
/// race, deliberately owned by the domain rather than borrowed from whatever
/// persistence library happens to be behind <c>ISeatRepository</c>. Adapters
/// translate their storage-specific failure into this; callers catch this and
/// never learn what the storage was. Losing the race is an expected outcome on
/// this path, not a fault — a caller that catches it is handling business flow,
/// not an error.
/// </remarks>
public sealed class ConcurrentSeatModificationException : Exception
{
    /// <summary>Creates the exception for a given seat.</summary>
    public ConcurrentSeatModificationException(Guid seatId)
        : base($"Seat {seatId} was modified concurrently and the write was rejected.")
        => SeatId = seatId;

    /// <summary>Creates the exception, preserving the adapter-level cause.</summary>
    public ConcurrentSeatModificationException(Guid seatId, Exception innerException)
        : base($"Seat {seatId} was modified concurrently and the write was rejected.", innerException)
        => SeatId = seatId;

    /// <summary>The seat whose write was rejected.</summary>
    public Guid SeatId { get; }
}
