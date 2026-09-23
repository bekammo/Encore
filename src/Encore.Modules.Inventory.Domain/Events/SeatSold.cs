using Encore.Shared;

namespace Encore.Modules.Inventory.Domain.Events;

/// <summary>A held seat was sold. Terminal.</summary>
public sealed record SeatSold(
    Guid SeatId,
    Guid EventId,
    Guid ClientId,
    DateTime OccurredAt) : IDomainEvent;
