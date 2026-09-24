using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.Application;

/// <summary>
/// The only lock Inventory takes (005). Seats have none: <c>xmin</c> settles each race (004).
/// </summary>
internal static class ClientHoldLock
{
    /// <summary>
    /// One write attempt, not the hold duration: it only covers a process that died mid-write.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    public static string Resource(Guid clientId, Guid eventId) =>
        $"client:{clientId}:event:{eventId}";

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
