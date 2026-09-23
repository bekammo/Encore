using System.Diagnostics;
using Encore.Modules.Inventory.Adapters.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Encore.Modules.Inventory.Adapters.Messaging;

/// <summary>
/// Delivers undelivered rows from <c>inventory.outbox_messages</c> to their handlers,
/// with exponential backoff and a dead letter after <see cref="OutboxOptions.MaxAttempts"/>.
/// </summary>
/// <remarks>
/// At-least-once, and no seat invariant depends on it running. Events from one
/// transaction arrive in order; across transactions there is no ordering promise, and
/// a failing message is overtaken rather than blocking the queue.
/// </remarks>
internal sealed class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    OutboxEventCatalog catalog,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    /// <summary>
    /// Raw SQL because EF Core cannot express a locking clause. <c>SKIP LOCKED</c> lets
    /// several dispatchers share the table; claiming on <c>ProcessedAt IS NULL</c> rather
    /// than a last-seen id means a late-committing row is never skipped.
    /// </summary>
    private const string ClaimSql = $$"""
        SELECT * FROM "{{InventoryPersistence.Schema}}"."outbox_messages"
        WHERE "ProcessedAt" IS NULL
          AND "NextAttemptAt" <= {0}
          AND "Attempts" < {1}
        ORDER BY "Id"
        LIMIT {2}
        FOR UPDATE SKIP LOCKED
        """;

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly OutboxEventCatalog _catalog = catalog;
    private readonly OutboxOptions _options = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<OutboxDispatcher> _logger = logger;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Inventory outbox dispatcher started: batch {BatchSize}, poll {PollInterval}, max {MaxAttempts} attempts.",
            _options.BatchSize,
            _options.PollInterval,
            _options.MaxAttempts);

        while (!stoppingToken.IsCancellationRequested)
        {
            int claimed;

            try
            {
                claimed = await DispatchBatchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // An exception escaping ExecuteAsync would stop delivery for the life of the process.
                _logger.LogError(ex, "Outbox tick failed. Retrying after {PollInterval}.", _options.PollInterval);
                claimed = 0;
            }

            // A full batch means more is probably waiting, so loop straight away.
            if (claimed >= _options.BatchSize)
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

        _logger.LogInformation("Inventory outbox dispatcher stopped.");
    }

    /// <summary>Claims one batch, delivers it and records the outcome. Internal so tests can drive one tick.</summary>
    /// <returns>How many messages were claimed.</returns>
    internal async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        // One transaction, so the row locks are still held when the outcome is written.
        await using var transaction = await context.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var claimed = await context.OutboxMessages
            .FromSqlRaw(
                ClaimSql,
                _timeProvider.GetUtcNow().UtcDateTime,
                _options.MaxAttempts,
                _options.BatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (claimed.Count is 0)
        {
            return 0;
        }

        // Wall clock, not TimeProvider: this bounds how long real row locks are held.
        var started = Stopwatch.StartNew();
        var delivered = 0;

        foreach (var message in claimed)
        {
            if (started.Elapsed >= _options.MaxBatchDuration)
            {
                // Out of budget. The rest are untouched and will be claimed again next tick.
                _logger.LogWarning(
                    "Outbox tick spent its {MaxBatchDuration} budget after {Delivered} of {Claimed} messages. Committing and leaving the rest for the next tick.",
                    _options.MaxBatchDuration,
                    delivered,
                    claimed.Count);
                break;
            }

            await DeliverAsync(scope.ServiceProvider, message, cancellationToken).ConfigureAwait(false);
            delivered++;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return claimed.Count;
    }

    /// <summary>
    /// Hands one message to its handler under <see cref="OutboxOptions.DeliveryTimeout"/>.
    /// A handler that overruns fails like any other.
    /// </summary>
    private async Task DeliverAsync(
        IServiceProvider provider,
        OutboxMessage message,
        CancellationToken cancellationToken)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.DeliveryTimeout);

        try
        {
            await _catalog.DispatchAsync(provider, message, deadline.Token).ConfigureAwait(false);
            message.MarkProcessed(utcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down: leave the row as it was, without spending an attempt.
            throw;
        }
        catch (Exception ex)
        {
            var backoff = BackoffFor(message.Attempts);
            message.MarkFailed(utcNow, ex.ToString(), backoff);

            if (message.Attempts >= _options.MaxAttempts)
            {
                _logger.LogError(
                    ex,
                    "Outbox message {MessageId} ('{EventType}') dead-lettered after {Attempts} attempts.",
                    message.MessageId,
                    message.EventType,
                    message.Attempts);
            }
            else
            {
                _logger.LogWarning(
                    ex,
                    "Outbox message {MessageId} ('{EventType}') failed on attempt {Attempts}. Retrying in {Backoff}.",
                    message.MessageId,
                    message.EventType,
                    message.Attempts,
                    backoff);
            }
        }
    }

    /// <summary>Exponential backoff, capped. The exponent is clamped so the shift cannot overflow.</summary>
    private TimeSpan BackoffFor(int attemptsSoFar)
    {
        var exponent = Math.Min(attemptsSoFar, 16);
        var delay = _options.BaseBackoff * (1L << exponent);

        return delay > _options.MaxBackoff ? _options.MaxBackoff : delay;
    }
}
