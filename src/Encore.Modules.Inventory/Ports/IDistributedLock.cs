namespace Encore.Modules.Inventory.Ports;

/// <summary>
/// A short-lived lock across processes. Where something else guarantees correctness it
/// only reduces contention; for the per-client hold cap it is the only guard.
/// </summary>
/// <remarks>
/// Implementations report an unreachable lock service as <see cref="LockOutcome.Unavailable"/>
/// instead of throwing, so callers can carry on without it.
/// </remarks>
public interface IDistributedLock
{
    /// <summary>Tries to take the lock without waiting.</summary>
    /// <param name="resource">Key naming what is locked.</param>
    /// <param name="ttl">How long the lock survives if never released.</param>
    /// <returns>The outcome, with an ownership token when the lock was taken.</returns>
    Task<LockAcquisition> TryAcquireAsync(
        string resource,
        TimeSpan ttl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the lock if <paramref name="token"/> still owns it. Pass
    /// <see cref="CancellationToken.None"/>: a cancelled release strands the lock until its TTL.
    /// </summary>
    /// <returns>Whether this caller's lock was released.</returns>
    Task<bool> ReleaseAsync(
        string resource,
        string token,
        CancellationToken cancellationToken = default);
}
