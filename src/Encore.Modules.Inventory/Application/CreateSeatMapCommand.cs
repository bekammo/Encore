namespace Encore.Modules.Inventory.Application;

/// <summary>
/// A request to bring an event's seats into existence.
/// </summary>
/// <remarks>
/// Answers the question <c>DECISIONS.md</c> 005 left open — whether seats are
/// created individually or in bulk as part of an event's seat map — with: the
/// aggregate is per seat, the use case is bulk. A venue gets a layout in one
/// act; nobody creates a 2,000-seat arena one seat at a time.
/// </remarks>
/// <param name="EventId">The event the seats belong to.</param>
/// <param name="Count">How many seats to create.</param>
public sealed record CreateSeatMapCommand(Guid EventId, int Count);
