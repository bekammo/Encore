using Encore.Shared;

namespace Encore.Modules.Inventory.Domain.Events;

/// <summary>A seat was claimed for a client until <paramref name="HoldExpiresAt"/>.</summary>
public sealed record SeatHeld(
    Guid SeatId,
    Guid EventId,
    Guid ClientId,
    DateTime HoldExpiresAt,
    DateTime OccurredAt) : IDomainEvent;
