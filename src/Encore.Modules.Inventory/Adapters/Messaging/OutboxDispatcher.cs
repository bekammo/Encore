using System.Diagnostics;
using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Adapters.Scheduling;
using Encore.Modules.Inventory.Adapters.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Encore.Modules.Inventory.Adapters.Messaging;

/// <summary>
/// At least once, in no order a consumer may rely on: a failing message is overtaken rather
/// than blocking the queue, even by a row from its own transaction, and several dispatchers
/// share the table (024). No seat invariant depends on it running.
/// </summary>
internal sealed class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    OutboxEventCatalog catalog,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    // Raw SQL: EF Core cannot express FOR UPDATE SKIP LOCKED. ProcessedAt IS NULL rather than a
    // last-seen id, so a late-committing row is never skipped. Ordered as
    // ix_outbox_messages_unprocessed is, so LIMIT ends the scan; ordering by Id alone read the
    // whole due backlog every tick.
    private const string ClaimSql = $$"""
        SELECT * FROM "{{InventoryPersistence.Schema}}"."outbox_messages"
        WHERE "ProcessedAt" IS NULL
          AND "NextAttemptAt" <= {0}
          AND "Attempts" < {1}
        ORDER BY "NextAttemptAt", "Id"
        LIMIT {2}
        FOR UPDATE SKIP LOCKED
        """;

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly OutboxEventCatalog _catalog = catalog;
    private readonly OutboxOptions _options = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<OutboxDispatcher> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Inventory outbox dispatcher started: batch {BatchSize}, poll {PollInterval}, max {MaxAttempts} attempts.",
            _options.BatchSize,
            _options.PollInterval,
            _options.MaxAttempts);

        await PollingLoop.RunAsync(
            DispatchBatchAsync,
            _options.BatchSize,
            _options.PollInterval,
            _timeProvider,
            _logger,
            "Outbox tick",
            stoppingToken).ConfigureAwait(false);

        _logger.LogInformation("Inventory outbox dispatcher stopped.");
    }

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

        // Wall clock, not TimeProvider: this bounds how long real row locks are held (016).
        var started = Stopwatch.StartNew();
        var delivered = 0;

        foreach (var message in claimed)
        {
            if (started.Elapsed >= _options.MaxBatchDuration)
            {
                _logger.LogWarning(
                    "Outbox tick spent its {MaxBatchDuration} budget after {Delivered} of {Claimed} messages. Committing and leaving the rest for the next tick.",
                    _options.MaxBatchDuration,
                    delivered,
                    claimed.Count);
                break;
            }

            await DeliverAsync(message, cancellationToken).ConfigureAwait(false);
            delivered++;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // Claimed, not delivered: a batch the budget cut short still reads as full, so the
        // next tick starts at once.
        return claimed.Count;
    }

    private async Task DeliverAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        using var activity = StartDelivery(message);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.DeliveryTimeout);

        // A scope per message: a shared one would carry a failed handler's tracked insert into
        // the next delivery.
        await using var scope = _scopeFactory.CreateAsyncScope();

        try
        {
            await _catalog.DispatchAsync(scope.ServiceProvider, message, deadline.Token).ConfigureAwait(false);
            message.MarkProcessed(utcNow);

            RecordDelivery(message, "Delivered");
            InventoryTelemetry.OutboxDeliveryLag.Record(
                (utcNow - message.OccurredAt).TotalSeconds,
                new KeyValuePair<string, object?>("event_type", message.EventType));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down: leave the row as it was, spending no attempt. A deadline overrun
            // is not caught here; it fails like any handler error.
            throw;
        }
        catch (Exception ex)
        {
            var backoff = BackoffFor(message.Attempts);
            message.MarkFailed(utcNow, ex.ToString(), backoff);

            var deadLettered = message.Attempts >= _options.MaxAttempts;

            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            RecordDelivery(message, deadLettered ? "DeadLettered" : "Failed");

            if (deadLettered)
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

    private TimeSpan BackoffFor(int attemptsSoFar)
    {
        // Clamped so the shift cannot overflow.
        var exponent = Math.Min(attemptsSoFar, 16);
        var delay = _options.BaseBackoff * (1L << exponent);

        return delay > _options.MaxBackoff ? _options.MaxBackoff : delay;
    }

    /// <summary>
    /// Links to the trace that raised the event instead of joining it: that trace ended long
    /// ago, and a redelivery would give it a second child.
    /// </summary>
    internal static Activity? StartDelivery(OutboxMessage message)
    {
        ActivityLink[]? links = message.TraceParent is { } traceParent
            && ActivityContext.TryParse(traceParent, traceState: null, out var raisedBy)
                ? [new ActivityLink(raisedBy)]
                : null;

        var activity = InventoryTelemetry.Source.StartActivity(
            ActivityKind.Consumer,
            parentContext: default,
            links: links,
            name: $"outbox deliver {message.EventType}");

        activity?.SetTag("messaging.message.id", message.MessageId);
        activity?.SetTag("messaging.destination.name", message.EventType);
        activity?.SetTag("encore.outbox.attempts", message.Attempts);

        return activity;
    }

    private static void RecordDelivery(OutboxMessage message, string outcome) =>
        InventoryTelemetry.OutboxDeliveries.Add(
            1,
            new KeyValuePair<string, object?>("event_type", message.EventType),
            new KeyValuePair<string, object?>("outcome", outcome));
}
