namespace Encore.Modules.Inventory.Ports;

/// <summary>
/// A short-lived mutual-exclusion primitive across processes, used to serialise
/// concurrent attempts to mutate the same seat row.
/// </summary>
/// <remarks>
/// This is an optimisation, not a correctness mechanism. Correctness comes from
/// the optimistic concurrency token on the seat row; the lock exists to stop a
/// thousand simultaneous attempts on one popular seat from all reaching Postgres
/// and all but one losing there. Code that calls this must still handle losing
/// the race afterwards, and must still be correct if the lock silently does
/// nothing at all.
/// </remarks>
public interface IDistributedLock
{
    /// <summary>
    /// Attempts to take the lock on <paramref name="resource"/> without waiting.
    /// </summary>
    /// <param name="resource">Opaque key naming what is being locked.</param>
    /// <param name="ttl">
    /// How long the lock survives if it is never released — a safety net for a
    /// crashed holder, so it is sized to one write attempt (seconds), never to
    /// the business-level hold window.
    /// </param>
    /// <returns>
    /// An ownership token to pass to <see cref="ReleaseAsync"/>, or
    /// <see langword="null"/> if someone else holds the lock. No retrying and no
    /// backoff: deciding what to do when the lock is taken is the caller's call.
    /// </returns>
    Task<string?> TryAcquireAsync(string resource, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the lock, but only if <paramref name="token"/> still owns it.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if this caller's lock was released;
    /// <see langword="false"/> if it had already expired and possibly been taken
    /// by someone else — in which case releasing would have freed their lock,
    /// so nothing is done.
    /// </returns>
    Task<bool> ReleaseAsync(string resource, string token, CancellationToken cancellationToken = default);
}
