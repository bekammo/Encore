namespace Encore.Modules.Inventory.Application;

/// <summary>
/// A request to bring an event's seats into existence.
/// </summary>
/// <remarks>The aggregate is per seat; the use case is bulk.</remarks>
/// <param name="EventId">The event the seats belong to.</param>
/// <param name="Count">How many seats to create.</param>
public sealed record CreateSeatMapCommand(Guid EventId, int Count);
