using Encore.Modules.Inventory.Adapters.Telemetry;
using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.Adapters.Caching;

/// <summary>
/// Stops asking the lock service for a short while after it could not answer. During an
/// outage every attempt would be refused anyway; this refuses it without the round trip and
/// the exception. The first attempt after the window is the probe.
/// </summary>
/// <remarks>
/// Changes nothing a caller can rely on: "unavailable" already means "proceed without the
/// lock". Several callers may probe at once when a window ends; with fail-fast connections a
/// probe is cheap, so no one is elected.
/// </remarks>
internal sealed class CooldownDistributedLock(
    IDistributedLock inner,
    TimeSpan cooldown,
    TimeProvider timeProvider) : IDistributedLock
{
    /// <summary>The outcome recorded when the cooldown answered instead of the service.</summary>
    internal const string CoolingDownOutcome = "CoolingDown";

    private readonly IDistributedLock _inner = inner;
    private readonly TimeSpan _cooldown = cooldown;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <summary>UTC ticks until which the service is not asked. Zero when it is answering.</summary>
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
    /// <remarks>Skipped while cooling down: the key has a TTL and frees itself.</remarks>
    public async Task<bool> ReleaseAsync(
        string resource,
        string token,
        CancellationToken cancellationToken = default)
    {
        if (CoolingDown())
        {
            InventoryTelemetry.RecordLock("release", CoolingDownOutcome);

            return false;
        }

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
