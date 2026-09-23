using Encore.Modules.Inventory.Adapters.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Encore.Modules.Inventory.Adapters.Messaging;

/// <summary>
/// Deletes outbox rows delivered longer ago than the retention window. Cleanup only.
/// A bulk delete is right here: removing a delivered row announces nothing. Undelivered
/// messages and dead letters are never deleted.
/// </summary>
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
                _logger.LogError(ex, "Outbox retention sweep failed. Retrying after {PollInterval}.", _options.PollInterval);
                deleted = 0;
            }

            // A full batch means there is more to remove.
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

    /// <summary>Deletes one batch of old delivered rows. Internal so tests can drive one pass.</summary>
    /// <returns>How many rows were removed.</returns>
    internal async Task<int> SweepBatchAsync(CancellationToken cancellationToken)
    {
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - _options.KeepDelivered;

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        // The subquery bounds the batch; EF has no Take() on ExecuteDelete.
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
