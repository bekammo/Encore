using Encore.Shared;

namespace Encore.Modules.Inventory.Domain.Events;

/// <summary>
/// Raised when a held seat has been converted to a confirmed sale. Terminal:
/// a sold seat never returns to the available pool.
/// </summary>
/// <param name="SeatId">The seat that was sold.</param>
/// <param name="EventId">The concert the seat belongs to.</param>
/// <param name="ClientId">Who bought it.</param>
/// <param name="OccurredAt">When the sale was confirmed.</param>
public sealed record SeatSold(
    Guid SeatId,
    Guid EventId,
    Guid ClientId,
    DateTime OccurredAt) : IDomainEvent;
