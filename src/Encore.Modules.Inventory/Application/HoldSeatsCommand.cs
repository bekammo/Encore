namespace Encore.Modules.Inventory.Application;

/// <summary>
/// A request to put several seats in one client's basket, written together.
/// </summary>
/// <remarks>
/// Every seat is attempted and answered on its own, so a refused seat does not
/// cost the client the ones that could be held (023). What the batch buys is one
/// round trip and one transaction for all of them. See <c>DECISIONS.md</c> 076.
/// </remarks>
/// <param name="EventId">The event every seat belongs to. Checked, never trusted.</param>
/// <param name="SeatIds">The seats, distinct, in the order the cap is applied.</param>
/// <param name="ClientId">Who is claiming them.</param>
public sealed record HoldSeatsCommand(Guid EventId, IReadOnlyList<Guid> SeatIds, Guid ClientId);
