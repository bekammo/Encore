namespace Encore.Modules.Inventory.Contracts;

/// <summary>The outcome of a hold attempt.</summary>
/// <param name="Status">What happened. Callers switch over this.</param>
/// <param name="HoldExpiresAt">
/// When the hold lapses, always UTC. Present only when <paramref name="Status"/>
/// is <see cref="HoldSeatStatus.Held"/>; null otherwise.
/// </param>
public sealed record HoldSeatResponse(HoldSeatStatus Status, DateTime? HoldExpiresAt = null);
