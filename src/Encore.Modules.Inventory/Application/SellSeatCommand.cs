namespace Encore.Modules.Inventory.Application;

/// <summary>
/// A request to convert one client's live hold into a confirmed sale.
/// </summary>
/// <remarks>
/// Carries <see cref="EventId"/> for the same reason
/// <see cref="HoldSeatCommand"/> does: it is checked against the seat rather
/// than trusted, so a seat cannot be bought through the wrong event's route and
/// nobody can learn which seat ids exist by asking about an event they are not
/// looking at.
/// </remarks>
/// <param name="EventId">The event the seat belongs to.</param>
/// <param name="SeatId">The seat being bought.</param>
/// <param name="ClientId">Who is buying it. Must be the client currently holding it.</param>
public sealed record SellSeatCommand(Guid EventId, Guid SeatId, Guid ClientId);
