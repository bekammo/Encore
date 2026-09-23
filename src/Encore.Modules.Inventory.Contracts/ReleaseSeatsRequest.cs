namespace Encore.Modules.Inventory.Contracts;

/// <summary>Asks Inventory to give several held seats back, in one round trip.</summary>
/// <remarks>Each seat is answered on its own.</remarks>
/// <param name="EventId">The event every seat is expected to belong to. Checked, never trusted.</param>
/// <param name="SeatIds">The seats to release, distinct.</param>
/// <param name="ClientId">Who is asking. Only the holding client may release.</param>
public sealed record ReleaseSeatsRequest(Guid EventId, IReadOnlyList<Guid> SeatIds, Guid ClientId);
