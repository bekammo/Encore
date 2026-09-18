namespace Encore.Modules.Inventory.Ports;

/// <summary>
/// A short-lived mutual-exclusion primitive across processes, used to serialise
/// concurrent attempts to mutate the same resource.
/// </summary>
/// <remarks>
/// <para>
/// This is an optimisation wherever something else already guarantees
/// correctness, and it is the only guard wherever nothing does. Seat writes are
/// the first kind — the optimistic concurrency token on the seat row settles
/// every race, and the lock exists only to stop a thousand simultaneous attempts
/// on one popular seat from all reaching Postgres and all but one losing there.
/// The per-client hold cap is the second kind: it spans several rows, so no row's
/// token can carry it.
/// </para>
/// <para>
/// Those two kinds need different answers when the lock is not granted, which is
/// why <see cref="TryAcquireAsync"/> returns a <see cref="LockAcquisition"/>
/// rather than a token or nothing. A caller with a backstop can proceed on any
/// outcome; a caller without one has to know whether it is looking at contention
/// or at an outage.
/// </para>
/// <para>
/// Implementations report a failure to reach the locking service as
/// <see cref="LockOutcome.Unavailable"/> rather than throwing. A caller must be
/// able to carry on when the lock service is gone, and an exception escaping
/// this port would take the whole operation with it — including operations whose
/// correctness never depended on the lock.
/// </para>
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
    /// The outcome, with an ownership token when the lock was taken. No retrying
    /// and no backoff: deciding what to do when the lock is not granted is the
    /// caller's call.
    /// </returns>
    Task<LockAcquisition> TryAcquireAsync(
        string resource,
        TimeSpan ttl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the lock, but only if <paramref name="token"/> still owns it.
    /// </summary>
    /// <remarks>
    /// Callers release from a <c>finally</c>, after the work is already done, so
    /// they should pass <see cref="CancellationToken.None"/> rather than the
    /// request's token. Cancelling a release does not undo the work — it just
    /// leaves the lock standing until its TTL runs out, holding everyone else
    /// off a resource nobody is using.
    /// </remarks>
    /// <returns>
    /// <see langword="true"/> if this caller's lock was released;
    /// <see langword="false"/> if it had already expired and possibly been taken
    /// by someone else — in which case releasing would have freed their lock, so
    /// nothing is done — or if the locking service could not be reached, in which
    /// case the lock will lapse on its own when the TTL runs out.
    /// </returns>
    Task<bool> ReleaseAsync(
        string resource,
        string token,
        CancellationToken cancellationToken = default);
}
