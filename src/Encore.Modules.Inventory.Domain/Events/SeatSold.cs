using Encore.Shared;

namespace Encore.Modules.Inventory.Domain.Events;

public sealed record SeatSold(
    Guid SeatId,
    Guid EventId,
    Guid ClientId,
    DateTime OccurredAt) : IDomainEvent;
