namespace Encore.Modules.Inventory.Contracts;

/// <summary>The outcome of a release attempt.</summary>
/// <param name="Status">What happened. Callers switch over this.</param>
public sealed record ReleaseSeatResponse(ReleaseSeatStatus Status);
