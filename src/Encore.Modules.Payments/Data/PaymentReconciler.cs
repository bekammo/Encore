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

internal sealed class PaymentReconciler(
    IServiceScopeFactory scopeFactory,
    SimulatedPaymentGateway gateway,
    IOptions<PaymentReconciliationOptions> options,
    TimeProvider timeProvider,
    ILogger<PaymentReconciler> logger) : BackgroundService
{
    /// <summary>Arbitrary, but no other job's advisory lock in the same database may use it.</summary>
    internal const long LeaseKey = 3_811_030_057;

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly SimulatedPaymentGateway _gateway = gateway;
    private readonly PaymentReconciliationOptions _options = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<PaymentReconciler> _logger = logger;

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

            // Sleep even after a full batch: unresolved rows stay first in line, and asking again
            // at once would only hammer the gateway (014).
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

    internal static Expression<Func<Payment, bool>> Overdue(DateTime cutoff) =>
        payment => (payment.Status == PaymentStatus.TimedOut && payment.ResolvedAt <= cutoff)
            || (payment.Status == PaymentStatus.Pending && payment.AttemptedAt <= cutoff);

    // Ids only: each row is settled in its own transaction, so one lost race spoils no other.
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

    private async Task<bool> ReconcileAsync(Guid paymentId, DateTime cutoff, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        await using var transaction = await context.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Held across both gateway calls until the commit (022): a confirm retrying this row waits,
        // then loses on xmin, instead of re-authorising funds just released.
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

        DateTime Now() => _timeProvider.GetUtcNow().UtcDateTime;

        // Pending this long means a crash lost the answer: claim it as timed out (022).
        if (payment.Status is PaymentStatus.Pending)
        {
            payment.TimeOut(Now());
        }

        var (record, reference) = await _gateway
            .LookUpAsync(payment.IdempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        switch (record)
        {
            // Nothing learned, but a claimed pending row is still saved as timed out.
            case GatewayRecord.Unknown:
                _logger.LogDebug(
                    "Payment {PaymentId} is still unresolved: the gateway did not answer the lookup.",
                    payment.Id);
                await CommitAsync(context, transaction).ConfigureAwait(false);
                return false;

            case GatewayRecord.Authorized:
                // Released, not recorded as Authorized: no order will capture it (014).
                if (await _gateway.VoidAsync(reference!, cancellationToken).ConfigureAwait(false)
                    is GatewayOutcome.TimedOut)
                {
                    // Left timed out, so a later sweep finds the hold and voids it again.
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

    // Not cancellable: by now the gateway may have acted on the row (022).
    private static async Task CommitAsync(PaymentsDbContext context, IDbContextTransaction transaction)
    {
        await context.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
