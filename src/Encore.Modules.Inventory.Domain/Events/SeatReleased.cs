using Encore.Shared;

namespace Encore.Modules.Inventory.Domain.Events;

public sealed record SeatReleased(
    Guid SeatId,
    Guid EventId,
    Guid ClientId,
    SeatReleaseReason Reason,
    DateTime OccurredAt) : IDomainEvent;
