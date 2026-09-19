namespace Encore.Modules.Inventory.Contracts;

/// <summary>The outcome of a sale attempt.</summary>
/// <param name="Status">What happened. Callers switch over this.</param>
public sealed record SellSeatResponse(SellSeatStatus Status);
