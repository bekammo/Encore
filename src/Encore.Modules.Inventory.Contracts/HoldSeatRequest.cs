namespace Encore.Modules.Inventory.Contracts;

/// <summary>Asks Inventory to hold one seat for one client.</summary>
/// <param name="EventId">
/// The event the seat is expected to belong to. Checked against the seat and
/// never trusted: a mismatch is reported as <see cref="HoldSeatStatus.SeatNotFound"/>,
/// deliberately indistinguishable from a seat that does not exist.
/// </param>
/// <param name="SeatId">The seat to hold.</param>
/// <param name="ClientId">Who is asking. Holds are per client, and so is the hold cap.</param>
public sealed record HoldSeatRequest(Guid EventId, Guid SeatId, Guid ClientId);
