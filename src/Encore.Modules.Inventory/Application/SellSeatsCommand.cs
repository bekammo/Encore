namespace Encore.Modules.Inventory.Application;

/// <summary>
/// A request to convert several of one client's live holds into sales, all of
/// them or none.
/// </summary>
/// <remarks>
/// All or none because a sold seat is terminal: selling three of an order's four
/// seats and then refusing the fourth leaves three seats nobody can buy and an
/// order nobody paid for. See <c>DECISIONS.md</c> 076.
/// </remarks>
/// <param name="EventId">The event every seat belongs to. Checked, never trusted.</param>
/// <param name="SeatIds">The seats, distinct.</param>
/// <param name="ClientId">Who is buying them. Must hold every one, live.</param>
public sealed record SellSeatsCommand(Guid EventId, IReadOnlyList<Guid> SeatIds, Guid ClientId);
