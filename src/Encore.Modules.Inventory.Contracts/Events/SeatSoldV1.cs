namespace Encore.Modules.Inventory.Contracts.Events;

/// <summary>
/// Published when a held seat has been converted to a confirmed sale. Terminal —
/// no later event ever moves this seat back.
/// </summary>
/// <remarks>
/// The one event with a consumer today. Notifications writes a row for it; the
/// other two are published because history cannot be backfilled (007), not because
/// anything reads them yet.
/// </remarks>
/// <param name="SeatId">The seat that was sold.</param>
/// <param name="EventId">The concert the seat belongs to.</param>
/// <param name="ClientId">Who bought it.</param>
/// <param name="OccurredAt">When the sale was confirmed. UTC.</param>
public sealed record SeatSoldV1(
    Guid SeatId,
    Guid EventId,
    Guid ClientId,
    DateTime OccurredAt);
