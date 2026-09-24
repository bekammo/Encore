using Encore.Shared;

namespace Encore.Modules.Inventory.Domain.Events;

public sealed record SeatHeld(
    Guid SeatId,
    Guid EventId,
    Guid ClientId,
    DateTime HoldExpiresAt,
    DateTime OccurredAt) : IDomainEvent;
