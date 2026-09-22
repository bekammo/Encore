using Encore.Modules.Inventory.Adapters.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Encore.Modules.Inventory.Adapters.Messaging;

/// <summary>
/// Deletes outbox rows that were delivered longer ago than the retention window.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cleanup, on the same terms as the expired-hold sweep (007).</b> Nothing waits
/// on it and no invariant may come to depend on it: a row this has not reached yet
/// is a delivered message sitting in a table, and the only thing its absence costs
/// is disk. If a test ever cannot pass with this disabled, the design is broken.
/// </para>
/// <para>
/// <b>A bulk delete, and here that is the right shape</b> — which is worth saying
/// because <c>ExpiredHoldSweeper</c> goes the other way and loads each aggregate.
/// The difference is what a row means. Expiring a hold is a state change somebody
/// downstream needs to hear about, so it goes through <c>Seat</c> and raises
/// <c>SeatReleased(Expired)</c> (062). Deleting a delivered outbox row announces
/// nothing to anybody; there is no aggregate, no event and no invariant, so
/// <c>ExecuteDeleteAsync</c> is exactly the mechanism the job wants.
/// </para>
/// <para>
/// <b>Delivered rows only, by <c>ProcessedAt</c>, never by age of the event.</b> An
/// undelivered message is work no matter how old it is, and a dead letter is the
/// evidence that something never arrived — deleting either by age would quietly
/// destroy the thing somebody would come looking for. See
/// <see cref="OutboxRetentionOptions.KeepDelivered"/>.
/// </para>
/// </remarks>
internal sealed class OutboxRetentionSweeper(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxRetentionOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxRetentionSweeper> logger) : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly OutboxRetentionOptions _options = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<OutboxRetentionSweeper> _logger = logger;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Outbox retention sweep started: keeping delivered messages for {KeepDelivered}, batch {BatchSize}, poll {PollInterval}.",
            _options.KeepDelivered,
            _options.BatchSize,
            _options.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            int deleted;

            try
            {
                deleted = await SweepBatchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The loop outlives anything one pass can throw, for the reason the
                // dispatcher gives: ending a BackgroundService silently stops it for
                // the life of the process, and a job nobody can tell is dead is worse
                // than a slow one.
                _logger.LogError(ex, "Outbox retention sweep failed. Retrying after {PollInterval}.", _options.PollInterval);
                deleted = 0;
            }

            // A full batch means there is more to remove, so go straight round again;
            // every row this pass touched is gone, so looping makes definite progress
            // rather than re-asking an unanswerable question.
            if (deleted >= _options.BatchSize)
            {
                continue;
            }

            try
            {
                await Task.Delay(_options.PollInterval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Outbox retention sweep stopped.");
    }

    /// <summary>Deletes one batch of expired, delivered rows.</summary>
    /// <returns>How many rows were removed.</returns>
    /// <remarks>
    /// Internal so the integration tests can drive one pass deterministically, for
    /// the reason <c>OutboxDispatcher.DispatchBatchAsync</c> gives.
    /// </remarks>
    internal async Task<int> SweepBatchAsync(CancellationToken cancellationToken)
    {
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - _options.KeepDelivered;

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        // The subquery bounds the delete, which the outer ExecuteDelete cannot do on
        // its own: EF has no Take() on a delete, and an unbounded first run against a
        // table nobody has pruned is precisely the long transaction 069 just spent an
        // entry bounding elsewhere.
        var doomed = context.OutboxMessages
            .Where(message => message.ProcessedAt != null && message.ProcessedAt < cutoff)
            .OrderBy(message => message.ProcessedAt)
            .Take(_options.BatchSize)
            .Select(message => message.Id);

        var deleted = await context.OutboxMessages
            .Where(message => doomed.Contains(message.Id))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        if (deleted > 0)
        {
            _logger.LogInformation(
                "Outbox retention sweep deleted {Deleted} messages delivered before {Cutoff:o}.",
                deleted,
                cutoff);
        }

        return deleted;
    }
}
