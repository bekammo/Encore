using System.Security.Cryptography;
using System.Text;
using Encore.Modules.Payments.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Encore.Modules.Payments.Simulation;

/// <summary>
/// Remembers its decisions in <c>payments.gateway_ledger</c>, so every process asks the same
/// gateway and a restart forgets nothing (014).
/// </summary>
internal sealed class SimulatedPaymentGateway(
    IServiceScopeFactory scopeFactory,
    IOptions<PaymentSimulationOptions> options,
    TimeProvider timeProvider)
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly PaymentSimulationOptions _options = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly Random? _seeded =
        options.Value.Seed is { } seed ? new Random(seed) : null;
    private readonly Lock _gate = new();

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

        // Drawn under one lock, so no other call's draw lands between the two.
        lock (_gate)
        {
            unanswered = NextDoubleLocked() < _options.TimeoutRate;
            requestLost = unanswered && NextDoubleLocked() < _options.LostRequestRate;
        }

        if (requestLost)
        {
            return (GatewayOutcome.TimedOut, null);
        }

        var outcome = await RememberAsync(idempotencyKey, cancellationToken).ConfigureAwait(false);

        if (unanswered)
        {
            return (GatewayOutcome.TimedOut, null);
        }

        return outcome is GatewayOutcome.Succeeded
            ? (outcome, ReferenceFor(idempotencyKey))
            : (outcome, null);
    }

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

    public async Task<GatewayOutcome> CaptureAsync(
        string gatewayReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayReference);

        await DelayAsync(cancellationToken).ConfigureAwait(false);

        return HangsUp() ? GatewayOutcome.TimedOut : GatewayOutcome.Succeeded;
    }

    /// <summary>The ledger row is kept, but nothing looks up a key after it has been reconciled.</summary>
    public async Task<GatewayOutcome> VoidAsync(
        string gatewayReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayReference);

        await DelayAsync(cancellationToken).ConfigureAwait(false);

        return HangsUp() ? GatewayOutcome.TimedOut : GatewayOutcome.Succeeded;
    }

    // If two calls race, the primary key lets one insert and the loser reads the winner's answer.
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
            RecordedAt = _timeProvider.GetUtcNow().UtcDateTime
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
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
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

        await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
    }

    // A seeded Random is not thread-safe.
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

    // For a caller already holding _gate.
    private double NextDoubleLocked() =>
        _seeded is null ? Random.Shared.NextDouble() : _seeded.NextDouble();

    // Derived from the key, so the same authorisation always answers with the same reference.
    private static string ReferenceFor(string idempotencyKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey));
        return $"auth_{Convert.ToHexStringLower(hash)[..16]}";
    }
}
