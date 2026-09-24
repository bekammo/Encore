namespace Encore.Modules.Inventory.Ports;

/// <summary>
/// The hold cap's only guard (005). An unreachable lock service answers
/// <see cref="LockOutcome.Unavailable"/> instead of throwing, so callers can carry on without it.
/// </summary>
public interface IDistributedLock
{
    /// <summary>Never waits for the lock.</summary>
    Task<LockAcquisition> TryAcquireAsync(
        string resource,
        TimeSpan ttl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases only while <paramref name="token"/> still owns the lock. Pass
    /// <see cref="CancellationToken.None"/>: a cancelled release strands the lock until its TTL.
    /// </summary>
    Task<bool> ReleaseAsync(
        string resource,
        string token,
        CancellationToken cancellationToken = default);
}
