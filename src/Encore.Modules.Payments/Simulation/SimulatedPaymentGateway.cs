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
/// <b>A timeout is two different events wearing one name, and since
/// <c>DECISIONS.md</c> 057 this class tells them apart.</b> A request lost on the
/// way to the gateway leaves nothing on record; one whose answer was lost coming
/// back leaves a decision the gateway will happily repeat when asked. The caller
/// cannot distinguish them — that is what makes a timeout ambiguous — but
/// <see cref="LookUpAsync"/> can, and reconciliation is built on exactly that.
/// <see cref="PaymentSimulationOptions.LostRequestRate"/> is which of the two a
/// given timeout turns out to have been.
/// </para>
/// <para>
/// <b>That supersedes an earlier note here saying a timeout is deliberately never
/// remembered.</b> Its reasoning was that remembering one would make the ambiguity
/// trivially resolvable and leave the retry path untested. That was wrong in an
/// instructive way: what the gateway records is not visible to the caller, so
/// recording it removes no ambiguity from the only side that experiences it — and a
/// retry under the same key getting a consistent answer back is precisely what a
/// real idempotent gateway does. Never recording it made one branch of
/// reconciliation unreachable instead.
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

        lock (_gate)
        {
            // Two independent misfortunes, decided in the order they would happen to
            // a real request. First: does the caller hear anything back at all?
            var unanswered = NextDoubleLocked() < _options.TimeoutRate;

            // Second, and only when it does not: was it the request that went
            // missing, or the answer? A request lost on the way there leaves the
            // gateway with nothing on record, which is what a later lookup reports
            // as NotFound.
            if (unanswered && NextDoubleLocked() < _options.LostRequestRate)
            {
                return (GatewayOutcome.TimedOut, null);
            }

            var outcome = RememberLocked(idempotencyKey);

            // It arrived and it was decided; the caller simply does not get to know.
            // That gap between what is true and what is known is the whole subject
            // of DECISIONS 031 and 057.
            if (unanswered)
            {
                return (GatewayOutcome.TimedOut, null);
            }

            return outcome is GatewayOutcome.Succeeded
                ? (outcome, ReferenceFor(idempotencyKey))
                : (outcome, null);
        }
    }

    /// <summary>
    /// Asks what the gateway knows about a key it may or may not have seen. A read:
    /// it decides nothing and records nothing.
    /// </summary>
    /// <remarks>
    /// The call reconciliation is built on, and the only way to tell the two halves
    /// of a timeout apart. It can itself fail to answer, in which case it says
    /// <see cref="GatewayRecord.Unknown"/> and the attempt stays exactly as
    /// unresolved as it was — reading a failed lookup as "nothing happened" would
    /// hand an order its live-attempt slot back while the customer's funds were
    /// still held.
    /// </remarks>
    public async Task<(GatewayRecord Record, string? Reference)> LookUpAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        await DelayAsync(cancellationToken).ConfigureAwait(false);

        if (HangsUp())
        {
            return (GatewayRecord.Unknown, null);
        }

        lock (_gate)
        {
            if (!_answered.TryGetValue(idempotencyKey, out var outcome))
            {
                return (GatewayRecord.NotFound, null);
            }

            return outcome is GatewayOutcome.Succeeded
                ? (GatewayRecord.Authorized, ReferenceFor(idempotencyKey))
                : (GatewayRecord.Declined, null);
        }
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
    /// <remarks>
    /// A released authorisation stays in the memory, so a lookup under the same key
    /// would still report it held. Nothing looks: an attempt is only ever reconciled
    /// while it is timed out, and resolving it moves it out of that set for good.
    /// Doing better would mean mapping a reference back to a key, and
    /// <see cref="ReferenceFor"/> is deliberately one-way.
    /// </remarks>
    public async Task<GatewayOutcome> VoidAsync(
        string gatewayReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayReference);

        await DelayAsync(cancellationToken).ConfigureAwait(false);

        return HangsUp() ? GatewayOutcome.TimedOut : GatewayOutcome.Succeeded;
    }

    /// <summary>
    /// The gateway's own decision for this key: the one already made if there is
    /// one, otherwise a fresh roll recorded for next time. Whether the caller gets
    /// to hear it is a separate question, and <see cref="AuthorizeAsync"/> answers
    /// it.
    /// </summary>
    /// <remarks>The caller holds <c>_gate</c>.</remarks>
    private GatewayOutcome RememberLocked(string idempotencyKey)
    {
        if (_answered.TryGetValue(idempotencyKey, out var already))
        {
            return already;
        }

        var outcome = NextDoubleLocked() < _options.DeclineRate
            ? GatewayOutcome.Declined
            : GatewayOutcome.Succeeded;

        _answered[idempotencyKey] = outcome;

        return outcome;
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
    /// The same draw, for a caller that already holds <c>_gate</c>. Separate from
    /// <see cref="NextDouble"/> rather than relying on the lock being reentrant: it
    /// is, but a detail this file's thread safety rests on should not be one a
    /// reader has to go and look up.
    /// </summary>
    private double NextDoubleLocked() =>
        _seeded is null ? Random.Shared.NextDouble() : _seeded.NextDouble();

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
