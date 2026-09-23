using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.Application;

/// <summary>Lock naming, lifetime and release, kept in one place.</summary>
internal static class SeatLocks
{
    /// <summary>
    /// How long a lock survives if never released. Sized to one write attempt, not to
    /// the hold duration: it only covers a process that died mid-write.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    /// <summary>The lock that serialises one client's hold-cap check at one event.</summary>
    public static string ForClient(Guid clientId, Guid eventId) =>
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
