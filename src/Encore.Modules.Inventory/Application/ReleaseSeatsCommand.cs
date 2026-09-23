namespace Encore.Modules.Inventory.Application;

/// <summary>
/// A request to give several held seats back, written together.
/// </summary>
/// <param name="EventId">The event every seat belongs to. Checked, never trusted.</param>
/// <param name="SeatIds">The seats, distinct.</param>
/// <param name="ClientId">Who is giving them up.</param>
public sealed record ReleaseSeatsCommand(Guid EventId, IReadOnlyList<Guid> SeatIds, Guid ClientId);
