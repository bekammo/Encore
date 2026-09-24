using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>
/// A lock that grants everything, so a result rests on the aggregate and <c>xmin</c> alone.
/// For tests where nothing needs serialising.
/// </summary>
internal sealed class AlwaysGrantingLock : IDistributedLock
{
    public Task<LockAcquisition> TryAcquireAsync(
        string resource,
        TimeSpan ttl,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(LockAcquisition.Acquired(Guid.NewGuid().ToString("N")));

    public Task<bool> ReleaseAsync(
        string resource,
        string token,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}
