using Encore.Modules.Payments.Models;
using Encore.Modules.Payments.Simulation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Encore.Modules.Payments.Data;

/// <summary>
/// Finds attempts the gateway never answered, asks it what actually happened, and
/// settles them. The path <c>DECISIONS.md</c> 031 described and parked; see 057.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it is for.</b> Without it a timed-out authorisation is live forever: it
/// holds the order's one live-attempt slot, and if the funds really were held it
/// holds those too, until the gateway expires them days later. 031 made the retry
/// path <i>safe</i> by reusing the row and its idempotency key. It could not make
/// the ambiguity <i>go away</i>, because only the gateway knows, and nothing asked.
/// This asks.
/// </para>
/// <para>
/// <b>It resolves; it does not authorise.</b> Every branch below is driven by
/// something read back from the gateway. The one write it makes to the outside
/// world is a void, and a void of an authorisation this module already knows it may
/// be holding. A reconciler that could authorise would be a second authority over
/// payment state, which is exactly what 031 refused to build half of.
/// </para>
/// <para>
/// <b>Why a hold it finds is released rather than captured.</b> 028 authorises,
/// sells, then captures, and a confirm whose authorisation times out returns before
/// selling anything. So a <see cref="PaymentStatus.TimedOut"/> row never has sold
/// seats behind it, and 028's own rule — a sale that does not complete voids the
/// authorisation — is the rule that applies. The void simply never happened,
/// because nobody knew there was anything to void.
/// </para>
/// <para>
/// <b>No lock, no claim, no <c>FOR UPDATE SKIP LOCKED</c>.</b> The outbox
/// dispatcher takes row locks because delivering a message twice is a real cost it
/// has to avoid. Here the expensive half is a <i>read</i> at the gateway, which two
/// instances may safely duplicate, and the write is arbitrated by <c>xmin</c> like
/// every other write in this module. Holding a Postgres row lock across a call to a
/// third party would be the worse trade by a distance: a confirm touching the same
/// row would block for as long as the gateway felt like taking.
/// </para>
/// <para>
/// <b>Nothing depends on this running</b>, which is 007's rule about the expiry
/// sweep arriving in a third place. Every Payments test in the tree predates it and
/// passes with it switched off; what it removes is a slowly-accumulating set of
/// unresolvable rows, not a correctness hole in the request path.
/// </para>
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
                // The loop must outlive anything one sweep can throw, for the reason
                // the dispatcher gives: ending a BackgroundService silently would
                // stop reconciliation for the life of the process.
                _logger.LogError(ex, "Reconciliation sweep failed. Retrying after {PollInterval}.", _options.PollInterval);
            }

            // Always sleep, even after a full batch — deliberately unlike the outbox
            // dispatcher, which goes straight round again. A message it fails to
            // deliver has its next attempt pushed into the future, so a full batch
            // there really does mean more work is ready. A row this fails to resolve
            // is still timed out, still old enough, and still first in the next
            // sweep's ordering, so looping on a full batch would mean hammering the
            // gateway with the same unanswerable questions as fast as it can refuse
            // to answer them.
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

    /// <summary>Sweeps one batch of timed-out attempts.</summary>
    /// <returns>How many were settled.</returns>
    /// <remarks>
    /// Internal rather than private so the integration tests can drive one sweep
    /// deterministically, for the reason <c>OutboxDispatcher.DispatchBatchAsync</c>
    /// gives: a test that started the hosted service and waited would be timing
    /// dependent, and a flaky test about an at-least-once mechanism reports failures
    /// indistinguishable from the thing it is meant to catch.
    /// </remarks>
    internal async Task<int> ReconcileBatchAsync(CancellationToken cancellationToken)
    {
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
    /// The attempts old enough to be worth asking about, oldest first.
    /// </summary>
    /// <remarks>
    /// Ids only, and read in a scope of its own that closes before any gateway call
    /// is made. Each attempt is then loaded, decided and saved on its own context,
    /// so one row losing a race on <c>xmin</c> leaves the rest of the sweep with a
    /// clean change tracker rather than a poisoned one.
    /// </remarks>
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

        // Re-read under the status filter rather than trusting the candidate list.
        // A confirm may have retried this row in the meantime, which moves it to
        // Pending and makes it the request path's business again — and the request
        // path, unlike this, has a customer waiting on the answer.
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

        // Read after the gateway has answered rather than before it was asked. The
        // calls are deliberately slow, and this instant is when the attempt was
        // resolved, not when the sweep started wondering.
        DateTime Resolved() => _timeProvider.GetUtcNow().UtcDateTime;

        switch (record)
        {
            // Still no answer. Nothing has been learned, so nothing is written and
            // the row stays exactly as ambiguous as it was.
            case GatewayRecord.Unknown:
                _logger.LogDebug(
                    "Payment {PaymentId} is still unresolved: the gateway did not answer the lookup.",
                    payment.Id);
                return false;

            case GatewayRecord.Authorized:
                // The funds are real, and this order is not going to use them.
                if (await _gateway.VoidAsync(reference!, cancellationToken).ConfigureAwait(false)
                    is GatewayOutcome.TimedOut)
                {
                    // Nothing is written. Recording the authorisation without having
                    // released it would swap one orphan for another, and the new one
                    // would be worse: an Authorized row nobody will ever capture and
                    // that no sweep looks at.
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
            // A confirm retried this row between the re-read and here, and it has
            // already asked the gateway the same question under the same key. Its
            // answer is at least as fresh as this one and it has a customer waiting
            // on it, so this one loses quietly.
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
