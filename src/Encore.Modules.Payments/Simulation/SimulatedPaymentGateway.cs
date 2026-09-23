using System.Security.Cryptography;
using System.Text;
using Encore.Modules.Payments.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Encore.Modules.Payments.Simulation;

/// <summary>
/// A fake payment provider that declines or hangs at configured rates after a configured
/// delay, so the rest of the system has to cope with an unreliable dependency.
/// </summary>
/// <remarks>
/// It honours idempotency keys and remembers its decisions in <c>payments.gateway_ledger</c>,
/// so every process asks the same gateway and a restart forgets nothing. A timeout is two
/// different events: a request lost on the way leaves no record, while an answer lost on the
/// way back leaves one that <see cref="LookUpAsync"/> can find. Seeded randomness is locked
/// because a seeded <see cref="Random"/> is not thread-safe.
/// </remarks>
internal sealed class SimulatedPaymentGateway(
    IServiceScopeFactory scopeFactory,
    IOptions<PaymentSimulationOptions> options,
    TimeProvider clock)
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly PaymentSimulationOptions _options = options.Value;
    private readonly TimeProvider _clock = clock;
    private readonly Random? _seeded =
        options.Value.Seed is { } seed ? new Random(seed) : null;

    /// <summary>
    /// Guards the random draws.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// Asks for funds to be held.
    /// </summary>
    /// <returns>The outcome, and the gateway's reference when it succeeded.</returns>
    public async Task<(GatewayOutcome Outcome, string? Reference)> AuthorizeAsync(
        string idempotencyKey,
        decimal amount,
        string currency,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        await DelayAsync(cancellationToken).ConfigureAwait(false);

        bool unanswered;
        bool requestLost;

        // Drawn together, so one call always consumes the same number of seeded draws.
        lock (_gate)
        {
            // Does the caller hear anything back?
            unanswered = NextDoubleLocked() < _options.TimeoutRate;

            // If not: was the request lost (no record) or only the answer?
            requestLost = unanswered && NextDoubleLocked() < _options.LostRequestRate;
        }

        if (requestLost)
        {
            return (GatewayOutcome.TimedOut, null);
        }

        var outcome = await RememberAsync(idempotencyKey, cancellationToken).ConfigureAwait(false);

        // It arrived and was decided; the caller just does not hear about it.
        if (unanswered)
        {
            return (GatewayOutcome.TimedOut, null);
        }

        return outcome is GatewayOutcome.Succeeded
            ? (outcome, ReferenceFor(idempotencyKey))
            : (outcome, null);
    }

    /// <summary>
    /// Asks what the gateway has on record for a key. Records nothing. A lookup can itself
    /// go unanswered, which is <see cref="GatewayRecord.Unknown"/>, never "nothing happened".
    /// </summary>
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

        var recorded = await ReadAsync(idempotencyKey, cancellationToken).ConfigureAwait(false);

        if (recorded is not { } outcome)
        {
            return (GatewayRecord.NotFound, null);
        }

        return outcome is GatewayOutcome.Succeeded
            ? (GatewayRecord.Authorized, ReferenceFor(idempotencyKey))
            : (GatewayRecord.Declined, null);
    }

    /// <summary>Takes funds that are being held.</summary>
    /// <remarks>Never declines: a known simplification.</remarks>
    public async Task<GatewayOutcome> CaptureAsync(
        string gatewayReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayReference);

        await DelayAsync(cancellationToken).ConfigureAwait(false);

        return HangsUp() ? GatewayOutcome.TimedOut : GatewayOutcome.Succeeded;
    }

    /// <summary>Releases funds that are being held, without taking them.</summary>
    /// <remarks>The ledger row is kept, but nothing looks up a key after it has been reconciled.</remarks>
    public async Task<GatewayOutcome> VoidAsync(
        string gatewayReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayReference);

        await DelayAsync(cancellationToken).ConfigureAwait(false);

        return HangsUp() ? GatewayOutcome.TimedOut : GatewayOutcome.Succeeded;
    }

    /// <summary>
    /// The gateway's decision for this key: the recorded one, or a fresh roll written down.
    /// If two calls race, the primary key lets one insert and the loser reads the winner's answer.
    /// </summary>
    private async Task<GatewayOutcome> RememberAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var ledger = context.Set<GatewayLedgerEntry>();

        var already = await ledger
            .AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.IdempotencyKey == idempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        if (already is not null)
        {
            return already.Outcome;
        }

        var outcome = NextDouble() < _options.DeclineRate
            ? GatewayOutcome.Declined
            : GatewayOutcome.Succeeded;

        ledger.Add(new GatewayLedgerEntry
        {
            IdempotencyKey = idempotencyKey,
            Outcome = outcome,
            RecordedAt = _clock.GetUtcNow().UtcDateTime
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return outcome;
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            context.ChangeTracker.Clear();

            var winner = await ledger
                .AsNoTracking()
                .SingleAsync(entry => entry.IdempotencyKey == idempotencyKey, cancellationToken)
                .ConfigureAwait(false);

            return winner.Outcome;
        }
    }

    /// <summary>
    /// What the gateway has on record for a key, or null.
    /// </summary>
    private async Task<GatewayOutcome?> ReadAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        var recorded = await context.Set<GatewayLedgerEntry>()
            .AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.IdempotencyKey == idempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        return recorded?.Outcome;
    }

    private static bool IsDuplicateKey(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: "23505" } postgres
        && postgres.ConstraintName == GatewayLedgerConfiguration.PrimaryKeyName;

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
    /// The same draw, for a caller already holding <c>_gate</c>.
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
