namespace Encore.Modules.Inventory.Endpoints;

/// <summary>A successful hold.</summary>
/// <remarks>
/// Named apart from <c>Contracts.HoldSeatResponse</c> deliberately. The two are
/// different shapes for different audiences — this one is the HTTP body of a
/// successful hold and carries no status, while the contract carries a status
/// and a nullable expiry because it has to describe refusals too. Sharing a name
/// across two namespaces in one assembly graph resolves by whichever namespace
/// the file happens to sit in, which is a resolution rule nobody should have to
/// know to read this.
/// </remarks>
/// <param name="SeatId">The seat now held.</param>
/// <param name="HoldExpiresAt">
/// When the hold lapses, in UTC. The caller has until then to complete checkout.
/// </param>
public sealed record HeldSeatResponse(Guid SeatId, DateTime HoldExpiresAt);
