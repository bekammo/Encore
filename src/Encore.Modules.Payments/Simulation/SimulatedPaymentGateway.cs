using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Encore.Modules.Payments.Simulation;

/// <summary>
/// A fake payment provider. Declines or hangs according to
/// <see cref="PaymentSimulationOptions"/>, after a configurable delay. There is no
/// real provider behind this and there is not meant to be — the point is to give
/// the rest of the system a dependency that is slow and unreliable in a way we
/// control.
/// </summary>
/// <remarks>
/// <para>
/// <b>It honours the idempotency key it is given, within a process.</b> A repeated
/// authorisation under a key it has already answered gets the same answer back
/// rather than a second hold. That is the whole reason the key exists, and a
/// simulator that ignored it would let the retry path pass tests it should fail.
/// The memory is per-instance and dies with the process, which is honest: a real
/// gateway remembers for days, and nothing here should come to depend on a
/// guarantee this one cannot make.
/// </para>
/// <para>
/// <b>Seeded randomness is locked, unseeded randomness is not.</b>
/// <see cref="Random.Shared"/> is already thread-safe; a seeded
/// <see cref="Random"/> is not, and an unsynchronised one under concurrent load
/// returns garbage rather than a reproducible sequence — which would quietly
/// defeat the only reason to set a seed.
/// </para>
/// </remarks>
internal sealed class SimulatedPaymentGateway(
    IOptions<PaymentSimulationOptions> options,
    TimeProvider clock)
{
    private readonly PaymentSimulationOptions _options = options.Value;
    private readonly TimeProvider _clock = clock;
    private readonly Random? _seeded =
        options.Value.Seed is { } seed ? new Random(seed) : null;

    private readonly Lock _gate = new();

    /// <summary>Answers already given, keyed by idempotency key.</summary>
    private readonly Dictionary<string, GatewayOutcome> _answered = [];

    /// <summary>
    /// Asks for funds to be held, returning a reference the caller can quote later.
    /// </summary>
    /// <returns>
    /// The outcome, and the gateway's reference when it succeeded. The reference is
    /// null for any other outcome, because there is nothing to refer to.
    /// </returns>
    public async Task<(GatewayOutcome Outcome, string? Reference)> AuthorizeAsync(
        string idempotencyKey,
        decimal amount,
        string currency,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        await DelayAsync(cancellationToken).ConfigureAwait(false);

        var outcome = Remember(idempotencyKey);

        return outcome is GatewayOutcome.Succeeded
            ? (outcome, ReferenceFor(idempotencyKey))
            : (outcome, null);
    }

    /// <summary>Takes funds that are being held.</summary>
    /// <remarks>
    /// Never declines: see <c>PaymentSimulationOptions.DeclineRate</c> and 032.
    /// </remarks>
    public async Task<GatewayOutcome> CaptureAsync(
        string gatewayReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayReference);

        await DelayAsync(cancellationToken).ConfigureAwait(false);

        return HangsUp() ? GatewayOutcome.TimedOut : GatewayOutcome.Succeeded;
    }

    /// <summary>Releases funds that are being held, without taking them.</summary>
    public async Task<GatewayOutcome> VoidAsync(
        string gatewayReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayReference);

        await DelayAsync(cancellationToken).ConfigureAwait(false);

        return HangsUp() ? GatewayOutcome.TimedOut : GatewayOutcome.Succeeded;
    }

    /// <summary>
    /// The answer for this key: the one already given if there is one, otherwise a
    /// fresh roll recorded for next time.
    /// </summary>
    /// <remarks>
    /// A timeout is deliberately <i>not</i> remembered. The whole point of the
    /// outcome is that the gateway's state is unknown, so a retry has to be allowed
    /// to land somewhere different — a gateway that answered "timed out" forever
    /// would make the ambiguity trivially resolvable and the retry path untested.
    /// </remarks>
    private GatewayOutcome Remember(string idempotencyKey)
    {
        lock (_gate)
        {
            if (_answered.TryGetValue(idempotencyKey, out var already))
            {
                return already;
            }

            var outcome = Roll();

            if (outcome is not GatewayOutcome.TimedOut)
            {
                _answered[idempotencyKey] = outcome;
            }

            return outcome;
        }
    }

    private GatewayOutcome Roll()
    {
        if (NextDouble() < _options.TimeoutRate)
        {
            return GatewayOutcome.TimedOut;
        }

        return NextDouble() < _options.DeclineRate
            ? GatewayOutcome.Declined
            : GatewayOutcome.Succeeded;
    }

    private bool HangsUp() => NextDouble() < _options.TimeoutRate;

    private async Task DelayAsync(CancellationToken cancellationToken)
    {
        var min = _options.MinLatency;
        var max = _options.MaxLatency < min ? min : _options.MaxLatency;

        if (max <= TimeSpan.Zero)
        {
            return;
        }

        var span = max - min;
        var delay = min + (span * NextDouble());

        await Task.Delay(delay, _clock, cancellationToken).ConfigureAwait(false);
    }

    private double NextDouble()
    {
        if (_seeded is null)
        {
            return Random.Shared.NextDouble();
        }

        lock (_gate)
        {
            return _seeded.NextDouble();
        }
    }

    /// <summary>
    /// A stable, opaque handle derived from the key, so the same authorisation
    /// always answers with the same reference.
    /// </summary>
    private static string ReferenceFor(string idempotencyKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey));
        return $"auth_{Convert.ToHexStringLower(hash)[..16]}";
    }
}
