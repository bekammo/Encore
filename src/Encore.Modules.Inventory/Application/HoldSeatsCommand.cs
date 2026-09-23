namespace Encore.Modules.Inventory.Application;

/// <summary>
/// A request to put several seats in one client's basket, written together.
/// </summary>
/// <remarks>Each seat is answered on its own; the successful holds are written together.</remarks>
/// <param name="EventId">The event every seat belongs to. Checked, never trusted.</param>
/// <param name="SeatIds">The seats, distinct, in the order the cap is applied.</param>
/// <param name="ClientId">Who is claiming them.</param>
public sealed record HoldSeatsCommand(Guid EventId, IReadOnlyList<Guid> SeatIds, Guid ClientId);
