namespace Encore.Modules.Inventory.Contracts;

/// <summary>Asks Inventory to hold several seats for one client, in one round trip.</summary>
/// <remarks>
/// Each seat is answered on its own: a refused seat does not cost the client the
/// seats that could be held.
/// </remarks>
/// <param name="EventId">Checked against each seat; a mismatch is reported as not found.</param>
/// <param name="SeatIds">Distinct. The hold cap is applied in this order.</param>
/// <param name="ClientId">Who is asking. Holds and the cap are per client.</param>
public sealed record HoldSeatsRequest(Guid EventId, IReadOnlyList<Guid> SeatIds, Guid ClientId);
