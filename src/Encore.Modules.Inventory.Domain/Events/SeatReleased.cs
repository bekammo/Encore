using Encore.Shared;

namespace Encore.Modules.Inventory.Domain.Events;

/// <summary>
/// Raised when a held seat returns to the available pool, whether because the
/// client cancelled or because the hold lapsed.
/// </summary>
/// <remarks>
/// There are three producers of this event: a client cancelling, lazy reclaim
/// during another client's <see cref="Seat.Hold"/>, and the background sweep.
/// <see cref="Reason"/> is what keeps them distinguishable in the log.
/// </remarks>
/// <param name="SeatId">The seat that was released.</param>
/// <param name="EventId">The concert the seat belongs to.</param>
/// <param name="ClientId">Whose hold ended.</param>
/// <param name="Reason">Whether the hold was cancelled or lapsed.</param>
/// <param name="OccurredAt">When the release happened.</param>
public sealed record SeatReleased(
    Guid SeatId,
    Guid EventId,
    Guid ClientId,
    SeatReleaseReason Reason,
    DateTime OccurredAt) : IDomainEvent;
