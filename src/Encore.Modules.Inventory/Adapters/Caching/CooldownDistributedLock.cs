using Encore.Modules.Inventory.Adapters.Telemetry;
using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.Adapters.Caching;

/// <summary>
/// Skips only acquires. A release always goes through: its caller took the lock before the
/// window opened, and skipping it would strand the key until its TTL. The first acquire after
/// the window is the probe; several may probe at once, and with fail-fast connections that is
/// cheap enough that none is elected.
/// </summary>
internal sealed class CooldownDistributedLock(
    IDistributedLock inner,
    TimeSpan cooldown,
    TimeProvider timeProvider) : IDistributedLock
{
    internal const string CoolingDownOutcome = "CoolingDown";

    private readonly IDistributedLock _inner = inner;
    private readonly TimeSpan _cooldown = cooldown;
    private readonly TimeProvider _timeProvider = timeProvider;

    private long _coolUntilTicks;

    /// <inheritdoc />
    public async Task<LockAcquisition> TryAcquireAsync(
        string resource,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        if (CoolingDown())
        {
            InventoryTelemetry.RecordLock("acquire", CoolingDownOutcome);

            return LockAcquisition.Unavailable;
        }

        var acquisition = await _inner.TryAcquireAsync(resource, ttl, cancellationToken).ConfigureAwait(false);

        Record(answered: acquisition.Outcome is not LockOutcome.Unavailable);
        InventoryTelemetry.RecordLock("acquire", acquisition.Outcome.ToString());

        return acquisition;
    }

    /// <inheritdoc />
    public async Task<bool> ReleaseAsync(
        string resource,
        string token,
        CancellationToken cancellationToken = default)
    {
        // A false release is not an outage: the key may simply have expired.
        var released = await _inner.ReleaseAsync(resource, token, cancellationToken).ConfigureAwait(false);

        InventoryTelemetry.RecordLock("release", released ? "Released" : "NotReleased");

        return released;
    }

    private bool CoolingDown()
    {
        var until = Volatile.Read(ref _coolUntilTicks);

        return until != 0 && _timeProvider.GetUtcNow().UtcTicks < until;
    }

    private void Record(bool answered)
    {
        if (answered)
        {
            Volatile.Write(ref _coolUntilTicks, 0);
        }
        else if (_cooldown > TimeSpan.Zero)
        {
            Volatile.Write(ref _coolUntilTicks, (_timeProvider.GetUtcNow() + _cooldown).UtcTicks);
        }
    }
}
