namespace Encore.Modules.Inventory.Endpoints;

/// <summary>Body of a request to create an event's seat map.</summary>
/// <param name="Count">How many seats to create.</param>
public sealed record CreateSeatMapRequest(int Count);
