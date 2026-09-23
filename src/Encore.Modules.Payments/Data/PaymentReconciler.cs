using System.Linq.Expressions;
using Encore.Modules.Payments.Models;
using Encore.Modules.Payments.Simulation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
/// funds, indefinitely. An attempt left pending by a crash between the gateway call and its
/// save is the same case, so it is claimed as timed out first. It never authorises; its only
/// outside write is releasing funds it finds held. Each row stays locked from the lookup to
/// the save, so a confirm that retries it meanwhile waits and then loses on <c>xmin</c>,
/// instead of re-authorising funds this has just released. A Postgres advisory lock per sweep
/// keeps two instances from duplicating work. Nothing depends on it running.
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

        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - _options.MinimumAge;
        var candidates = await CandidatesAsync(cutoff, cancellationToken).ConfigureAwait(false);

        if (candidates.Count is 0)
        {
            return 0;
        }

        var settled = 0;

        foreach (var id in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await ReconcileAsync(id, cutoff, cancellationToken).ConfigureAwait(false))
            {
                settled++;
            }
        }

        return settled;
    }

    /// <summary>
    /// Attempts the gateway may have decided without this system hearing the answer: timed
    /// out, or still pending since before <paramref name="cutoff"/>, which only a crash
    /// between the gateway call and its save leaves behind. The readiness check counts these too.
    /// </summary>
    internal static Expression<Func<Payment, bool>> Overdue(DateTime cutoff) =>
        payment => (payment.Status == PaymentStatus.TimedOut && payment.ResolvedAt <= cutoff)
            || (payment.Status == PaymentStatus.Pending && payment.AttemptedAt <= cutoff);

    /// <summary>
    /// Overdue attempts, oldest first. Each is then settled in its own scope and
    /// transaction, so one lost race does not affect the rest.
    /// </summary>
    private async Task<List<Guid>> CandidatesAsync(DateTime cutoff, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        return await context.Payments
            .AsNoTracking()
            .Where(Overdue(cutoff))
            .OrderBy(payment => payment.Status == PaymentStatus.TimedOut ? payment.ResolvedAt : payment.AttemptedAt)
            .Take(_options.BatchSize)
            .Select(payment => payment.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Asks the gateway about one attempt and records what it says.</summary>
    /// <returns>Whether the attempt was settled.</returns>
    private async Task<bool> ReconcileAsync(Guid paymentId, DateTime cutoff, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        await using var transaction = await context.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Held until the commit, across both gateway calls.
        await context.Database
            .ExecuteSqlAsync(
                $"""SELECT 1 FROM payments.payments WHERE "Id" = {paymentId} FOR UPDATE""",
                cancellationToken)
            .ConfigureAwait(false);

        // Re-read under the lock: a confirm may have retried or resumed this row since.
        var payment = await context.Payments
            .Where(Overdue(cutoff))
            .SingleOrDefaultAsync(candidate => candidate.Id == paymentId, cancellationToken)
            .ConfigureAwait(false);

        if (payment is null)
        {
            return false;
        }

        // Stamped after the slow gateway calls.
        DateTime Now() => _timeProvider.GetUtcNow().UtcDateTime;

        // A pending row this old lost its answer to a crash: from here it is a timeout.
        if (payment.Status is PaymentStatus.Pending)
        {
            payment.TimeOut(Now());
        }

        var (record, reference) = await _gateway
            .LookUpAsync(payment.IdempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        switch (record)
        {
            // Still no answer: nothing learned. A claimed pending row is still saved as timed out.
            case GatewayRecord.Unknown:
                _logger.LogDebug(
                    "Payment {PaymentId} is still unresolved: the gateway did not answer the lookup.",
                    payment.Id);
                await CommitAsync(context, transaction).ConfigureAwait(false);
                return false;

            case GatewayRecord.Authorized:
                // Funds are held that no order will use: release them.
                if (await _gateway.VoidAsync(reference!, cancellationToken).ConfigureAwait(false)
                    is GatewayOutcome.TimedOut)
                {
                    // Leave it timed out: an Authorized row nobody captures would be a worse orphan.
                    _logger.LogWarning(
                        "Payment {PaymentId} is holding funds under {GatewayReference}, but the void got no answer. Leaving it timed out.",
                        payment.Id,
                        reference);
                    await CommitAsync(context, transaction).ConfigureAwait(false);
                    return false;
                }

                payment.ResolveAsVoided(reference!, Now());
                break;

            case GatewayRecord.Declined:
                payment.ResolveAsDeclined(Now());
                break;

            case GatewayRecord.NotFound:
                payment.ResolveAsAbandoned(Now());
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(paymentId), record, "Unmapped gateway record.");
        }

        await CommitAsync(context, transaction).ConfigureAwait(false);

        _logger.LogInformation(
            "Payment {PaymentId} reconciled: the gateway reported {Record}, so the attempt is now {Status}.",
            payment.Id,
            record,
            payment.Status);

        return true;
    }

    /// <summary>
    /// Saves what the sweep decided and releases the row. Not cancellable: by now the gateway
    /// may have acted on it.
    /// </summary>
    private static async Task CommitAsync(PaymentsDbContext context, IDbContextTransaction transaction)
    {
        await context.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
