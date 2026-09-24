using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Adapters.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Encore.Modules.Inventory.Adapters.Messaging;

/// <summary>
/// A bulk delete, unlike the seat sweep: removing a delivered row announces nothing. Dead
/// letters and undelivered rows are never deleted.
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Outbox retention sweep started: keeping delivered messages for {KeepDelivered}, batch {BatchSize}, poll {PollInterval}.",
            _options.KeepDelivered,
            _options.BatchSize,
            _options.PollInterval);

        await PollingLoop.RunAsync(
            SweepBatchAsync,
            _options.BatchSize,
            _options.PollInterval,
            _timeProvider,
            _logger,
            "Outbox retention sweep",
            stoppingToken).ConfigureAwait(false);

        _logger.LogInformation("Outbox retention sweep stopped.");
    }

    internal async Task<int> SweepBatchAsync(CancellationToken cancellationToken)
    {
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - _options.KeepDelivered;

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        // EF has no Take() on ExecuteDelete, so a subquery bounds the batch. By Id, the primary
        // key in insert order, so the batch stops early; ProcessedAt has no index, and sorting by
        // it scanned the table.
        var doomed = context.OutboxMessages
            .Where(message => message.ProcessedAt != null && message.ProcessedAt < cutoff)
            .OrderBy(message => message.Id)
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
