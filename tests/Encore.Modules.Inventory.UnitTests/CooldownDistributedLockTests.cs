using System.Diagnostics.Metrics;
using Encore.Modules.Inventory.Adapters.Caching;
using Encore.Modules.Inventory.Adapters.Telemetry;
using Encore.Modules.Inventory.Ports;
using Microsoft.Extensions.Time.Testing;

namespace Encore.Modules.Inventory.UnitTests;

/// <summary>
/// After the lock service fails to answer, the lock stops asking it for a while and reports
/// "unavailable" itself. The first attempt after the window asks again.
/// </summary>
public class CooldownDistributedLockTests
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    private readonly ScriptedLock _inner = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    private CooldownDistributedLock Lock(TimeSpan? cooldown = null) =>
        new(_inner, cooldown ?? Cooldown, _clock);

    [Fact]
    public async Task TryAcquire_AfterARefusal_ShouldNotAskAgainInsideTheWindow()
    {
        var coolingLock = Lock();
        _inner.Next = LockAcquisition.Unavailable;

        await coolingLock.TryAcquireAsync("r", Ttl);
        _clock.Advance(Cooldown - TimeSpan.FromMilliseconds(1));
        _inner.Next = LockAcquisition.Acquired("token");

        var second = await coolingLock.TryAcquireAsync("r", Ttl);

        Assert.Equal(LockOutcome.Unavailable, second.Outcome);
        Assert.Equal(1, _inner.AcquireCalls);
    }

    [Fact]
    public async Task TryAcquire_AfterTheWindow_ShouldProbe()
    {
        var coolingLock = Lock();
        _inner.Next = LockAcquisition.Unavailable;

        await coolingLock.TryAcquireAsync("r", Ttl);
        _clock.Advance(Cooldown);
        _inner.Next = LockAcquisition.Acquired("token");

        var probe = await coolingLock.TryAcquireAsync("r", Ttl);

        Assert.Equal(LockOutcome.Acquired, probe.Outcome);
        Assert.Equal(2, _inner.AcquireCalls);
    }

    /// <summary>A probe that is answered ends the outage: the next attempt asks straight away.</summary>
    [Fact]
    public async Task TryAcquire_WhenTheProbeIsAnswered_ShouldAskEveryTimeAgain()
    {
        var coolingLock = Lock();
        _inner.Next = LockAcquisition.Unavailable;

        await coolingLock.TryAcquireAsync("r", Ttl);
        _clock.Advance(Cooldown);
        _inner.Next = LockAcquisition.HeldByAnother;
        await coolingLock.TryAcquireAsync("r", Ttl);

        var next = await coolingLock.TryAcquireAsync("r", Ttl);

        Assert.Equal(LockOutcome.HeldByAnother, next.Outcome);
        Assert.Equal(3, _inner.AcquireCalls);
    }

    /// <summary>A refused probe starts a new window.</summary>
    [Fact]
    public async Task TryAcquire_WhenTheProbeIsRefused_ShouldCoolDownAgain()
    {
        var coolingLock = Lock();
        _inner.Next = LockAcquisition.Unavailable;

        await coolingLock.TryAcquireAsync("r", Ttl);
        _clock.Advance(Cooldown);
        await coolingLock.TryAcquireAsync("r", Ttl);
        _clock.Advance(Cooldown / 2);

        await coolingLock.TryAcquireAsync("r", Ttl);

        Assert.Equal(2, _inner.AcquireCalls);
    }

    /// <summary>Somebody else holding the lock is an answer, not an outage.</summary>
    [Fact]
    public async Task TryAcquire_WhenHeldByAnother_ShouldKeepAsking()
    {
        var coolingLock = Lock();
        _inner.Next = LockAcquisition.HeldByAnother;

        await coolingLock.TryAcquireAsync("r", Ttl);
        await coolingLock.TryAcquireAsync("r", Ttl);

        Assert.Equal(2, _inner.AcquireCalls);
    }

    /// <summary>
    /// A caller with a token took its lock before the window opened. Skipping its release would
    /// strand the key until its TTL, and that client's next hold would be refused as in flight.
    /// </summary>
    [Fact]
    public async Task Release_WhileCoolingDown_ShouldStillAsk()
    {
        var coolingLock = Lock();
        _inner.Next = LockAcquisition.Unavailable;
        await coolingLock.TryAcquireAsync("r", Ttl);

        var released = await coolingLock.ReleaseAsync("r", "token");

        Assert.True(released);
        Assert.Equal(1, _inner.ReleaseCalls);
    }

    [Fact]
    public async Task Release_WhenAnswering_ShouldPassThrough()
    {
        var coolingLock = Lock();

        var released = await coolingLock.ReleaseAsync("r", "token");

        Assert.True(released);
        Assert.Equal(1, _inner.ReleaseCalls);
    }

    /// <summary>A zero cooldown is the control: every attempt asks.</summary>
    [Fact]
    public async Task TryAcquire_WithZeroCooldown_ShouldAskEveryTime()
    {
        var coolingLock = Lock(TimeSpan.Zero);
        _inner.Next = LockAcquisition.Unavailable;

        await coolingLock.TryAcquireAsync("r", Ttl);
        await coolingLock.TryAcquireAsync("r", Ttl);

        Assert.Equal(2, _inner.AcquireCalls);
    }

    /// <summary>What the cooldown answered is counted apart from what Redis answered.</summary>
    [Fact]
    public async Task TryAcquire_ShouldCountWhoAnswered()
    {
        var outcomes = new List<string>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == InventoryTelemetry.Name && instrument.Name == "encore.inventory.lock.attempts")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };

        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome")
                {
                    outcomes.Add((string)tag.Value!);
                }
            }
        });

        listener.Start();

        var coolingLock = Lock();
        _inner.Next = LockAcquisition.Unavailable;

        await coolingLock.TryAcquireAsync("r", Ttl);
        await coolingLock.TryAcquireAsync("r", Ttl);

        Assert.Equal([nameof(LockOutcome.Unavailable), CooldownDistributedLock.CoolingDownOutcome], outcomes);
    }

    private sealed class ScriptedLock : IDistributedLock
    {
        public LockAcquisition Next { get; set; } = LockAcquisition.Acquired("token");

        public int AcquireCalls { get; private set; }

        public int ReleaseCalls { get; private set; }

        public Task<LockAcquisition> TryAcquireAsync(
            string resource,
            TimeSpan ttl,
            CancellationToken cancellationToken = default)
        {
            AcquireCalls++;
            return Task.FromResult(Next);
        }

        public Task<bool> ReleaseAsync(
            string resource,
            string token,
            CancellationToken cancellationToken = default)
        {
            ReleaseCalls++;
            return Task.FromResult(true);
        }
    }
}
