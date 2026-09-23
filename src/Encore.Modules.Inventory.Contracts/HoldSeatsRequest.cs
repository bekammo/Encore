namespace Encore.Modules.Inventory.Contracts;

/// <summary>Asks Inventory to hold several seats for one client, in one round trip.</summary>
/// <remarks>
/// Each seat is answered on its own: a refused seat does not cost the client the
/// seats that could be held.
/// </remarks>
/// <param name="EventId">
/// The event every seat is expected to belong to. Checked against each seat and
/// never trusted: a mismatch is reported as <see cref="HoldSeatStatus.SeatNotFound"/>,
/// deliberately indistinguishable from a seat that does not exist.
/// </param>
/// <param name="SeatIds">
/// The seats to hold, distinct. The hold cap is applied in this order, so when it
/// runs out, the seats named last are the ones refused.
/// </param>
/// <param name="ClientId">Who is asking. Holds are per client, and so is the hold cap.</param>
public sealed record HoldSeatsRequest(Guid EventId, IReadOnlyList<Guid> SeatIds, Guid ClientId);
