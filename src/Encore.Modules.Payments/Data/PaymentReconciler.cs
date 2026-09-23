using Encore.Modules.Payments.Models;
using Encore.Modules.Payments.Simulation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Encore.Modules.Payments.Data;

/// <summary>
/// Finds authorisations the gateway never answered, asks the gateway what happened, and
/// settles them on the answer.
/// </summary>
/// <remarks>
/// Without it a timed-out attempt holds the order's live slot, and possibly the customer's
/// funds, indefinitely. It never authorises; its only outside write is releasing funds it
/// finds held, since a timed-out attempt never has sold seats behind it. A Postgres advisory
/// lock per sweep keeps two instances from duplicating work; <c>xmin</c> still guards each row.
/// Nothing depends on it running.
/// </remarks>
internal sealed class PaymentReconciler(
    IServiceScopeFactory scopeFactory,
    SimulatedPaymentGateway gateway,
    IOptions<PaymentReconciliationOptions> options,
    TimeProvider timeProvider,
    ILogger<PaymentReconciler> logger) : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly SimulatedPaymentGateway _gateway = gateway;
    private readonly PaymentReconciliationOptions _options = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<PaymentReconciler> _logger = logger;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Payment reconciler started: batch {BatchSize}, poll {PollInterval}, minimum age {MinimumAge}.",
            _options.BatchSize,
            _options.PollInterval,
            _options.MinimumAge);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileBatchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reconciliation sweep failed. Retrying after {PollInterval}.", _options.PollInterval);
            }

            // Always sleep, even after a full batch: unresolved rows are still first in
            // line, and asking again straight away would only hammer the gateway.
            try
            {
                await Task.Delay(_options.PollInterval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Payment reconciler stopped.");
    }

    /// <summary>
    /// This job's key in Postgres's advisory-lock namespace. Arbitrary but unique.
    /// </summary>
    internal const long LeaseKey = 3_811_030_057;

    /// <summary>
    /// Sweeps one batch under a transaction-scoped advisory lock, which is released even if
    /// the process dies. Another instance holding it means this batch is redundant, so it
    /// returns 0. Internal so tests can drive one sweep.
    /// </summary>
    /// <returns>How many were settled.</returns>
    internal async Task<int> ReconcileBatchAsync(CancellationToken cancellationToken)
    {
        using var leaseScope = _scopeFactory.CreateScope();
        var leaseContext = leaseScope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        await using var lease = await leaseContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // SqlQuery<T> for a scalar expects a column named "Value"; without the alias every
        // sweep throws 42703.
        var acquired = await leaseContext.Database
            .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({LeaseKey}) AS \"Value\"")
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!acquired)
        {
            _logger.LogDebug("Reconciliation sweep skipped: another instance already holds the lease.");
            return 0;
        }

        var candidates = await CandidatesAsync(cancellationToken).ConfigureAwait(false);

        if (candidates.Count is 0)
        {
            return 0;
        }

        var settled = 0;

        foreach (var id in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await ReconcileAsync(id, cancellationToken).ConfigureAwait(false))
            {
                settled++;
            }
        }

        return settled;
    }

    /// <summary>
    /// Timed-out attempts old enough to ask about, oldest first. Each is then settled in
    /// its own scope, so one lost race does not affect the rest.
    /// </summary>
    private async Task<List<Guid>> CandidatesAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - _options.MinimumAge;

        return await context.Payments
            .AsNoTracking()
            .Where(payment => payment.Status == PaymentStatus.TimedOut && payment.ResolvedAt <= cutoff)
            .OrderBy(payment => payment.ResolvedAt)
            .Take(_options.BatchSize)
            .Select(payment => payment.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Asks the gateway about one attempt and records what it says.</summary>
    /// <returns>Whether the attempt was settled.</returns>
    private async Task<bool> ReconcileAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        // Re-read with the status filter: a confirm may have retried this row since.
        var payment = await context.Payments
            .SingleOrDefaultAsync(
                candidate => candidate.Id == paymentId && candidate.Status == PaymentStatus.TimedOut,
                cancellationToken)
            .ConfigureAwait(false);

        if (payment is null)
        {
            return false;
        }

        var (record, reference) = await _gateway
            .LookUpAsync(payment.IdempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        // Stamped when resolved, after the slow gateway call.
        DateTime Resolved() => _timeProvider.GetUtcNow().UtcDateTime;

        switch (record)
        {
            // Still no answer: nothing learned, nothing written.
            case GatewayRecord.Unknown:
                _logger.LogDebug(
                    "Payment {PaymentId} is still unresolved: the gateway did not answer the lookup.",
                    payment.Id);
                return false;

            case GatewayRecord.Authorized:
                // Funds are held that no order will use: release them.
                if (await _gateway.VoidAsync(reference!, cancellationToken).ConfigureAwait(false)
                    is GatewayOutcome.TimedOut)
                {
                    // Write nothing: an Authorized row nobody captures would be a worse orphan.
                    _logger.LogWarning(
                        "Payment {PaymentId} is holding funds under {GatewayReference}, but the void got no answer. Leaving it timed out.",
                        payment.Id,
                        reference);
                    return false;
                }

                payment.ResolveAsVoided(reference!, Resolved());
                break;

            case GatewayRecord.Declined:
                payment.ResolveAsDeclined(Resolved());
                break;

            case GatewayRecord.NotFound:
                payment.ResolveAsAbandoned(Resolved());
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(paymentId), record, "Unmapped gateway record.");
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A confirm retried this row meanwhile; it has a customer waiting, so it wins.
            _logger.LogDebug(
                "Payment {PaymentId} was resolved by something else while this sweep was asking.",
                payment.Id);
            return false;
        }

        _logger.LogInformation(
            "Payment {PaymentId} reconciled: the gateway reported {Record}, so the attempt is now {Status}.",
            payment.Id,
            record,
            payment.Status);

        return true;
    }
}
