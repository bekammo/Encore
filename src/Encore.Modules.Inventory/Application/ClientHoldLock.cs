using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.Application;

/// <summary>
/// The lock that serialises one client's hold-cap check at one event (005): its name,
/// lifetime and release. It is the only lock Inventory takes; seats have none (004).
/// </summary>
internal static class ClientHoldLock
{
    /// <summary>
    /// How long a lock survives if never released. Sized to one write attempt, not to
    /// the hold duration: it only covers a process that died mid-write.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    /// <summary>The lock's resource name for one client at one event.</summary>
    public static string Resource(Guid clientId, Guid eventId) =>
        $"client:{clientId}:event:{eventId}";

    /// <summary>
    /// Releases a lock the caller took, if it took one. Takes no cancellation token: a
    /// client hanging up must not cancel the release and strand the lock.
    /// </summary>
    public static async Task ReleaseIfHeldAsync(
        this IDistributedLock distributedLock,
        string resource,
        LockAcquisition acquisition)
    {
        if (acquisition.Token is { } token)
        {
            await distributedLock
                .ReleaseAsync(resource, token, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }
}
