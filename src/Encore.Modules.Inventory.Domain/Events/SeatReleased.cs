using Encore.Shared;

namespace Encore.Modules.Inventory.Domain.Events;

/// <summary>
/// A held seat returned to the pool, because the client cancelled or the hold lapsed.
/// </summary>
public sealed record SeatReleased(
    Guid SeatId,
    Guid EventId,
    Guid ClientId,
    SeatReleaseReason Reason,
    DateTime OccurredAt) : IDomainEvent;
