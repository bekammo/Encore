using Encore.Shared;

namespace Encore.Modules.Inventory.Domain.Events;

/// <summary>
/// Raised when a seat has been successfully claimed for a client. Together with
/// <see cref="SeatReleased"/> and <see cref="SeatSold"/> this is what makes hold
/// history reconstructable from the event log, which is why there is no
/// SeatHold table.
/// </summary>
/// <param name="SeatId">The seat that was claimed.</param>
/// <param name="EventId">The concert the seat belongs to.</param>
/// <param name="ClientId">Who claimed it.</param>
/// <param name="HoldExpiresAt">When this claim lapses if nothing else happens.</param>
/// <param name="OccurredAt">When the claim was made.</param>
public sealed record SeatHeld(
    Guid SeatId,
    Guid EventId,
    Guid ClientId,
    DateTime HoldExpiresAt,
    DateTime OccurredAt) : IDomainEvent;
