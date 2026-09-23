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
/// Delivers undelivered rows from <c>inventory.outbox_messages</c> to their handlers,
/// with exponential backoff and a dead letter after <see cref="OutboxOptions.MaxAttempts"/>.
/// </summary>
/// <remarks>
/// At-least-once, in no order a consumer may rely on: a failing message is overtaken rather
/// than blocking the queue, even by a row from its own transaction, and several dispatchers
/// share the table (024). No seat invariant depends on it running.
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
    /// than a last-seen id means a late-committing row is never skipped. Ordered as
    /// <c>ix_outbox_messages_unprocessed</c> is, so the index supplies the order and the
    /// <c>LIMIT</c> ends the scan. Ordering by <c>Id</c> alone made every tick read the whole
    /// due backlog.
    /// </summary>
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

    /// <inheritdoc />
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

            await DeliverAsync(message, cancellationToken).ConfigureAwait(false);
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
    /// <remarks>
    /// Each message gets its own scope. A shared one would carry a failed handler's state, such
    /// as an insert still tracked by its context, into the next message's delivery.
    /// </remarks>
    private async Task DeliverAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        using var activity = StartDelivery(message);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.DeliveryTimeout);

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
            // Shutting down: leave the row as it was, without spending an attempt.
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

    /// <summary>Exponential backoff, capped. The exponent is clamped so the shift cannot overflow.</summary>
    private TimeSpan BackoffFor(int attemptsSoFar)
    {
        var exponent = Math.Min(attemptsSoFar, 16);
        var delay = _options.BaseBackoff * (1L << exponent);

        return delay > _options.MaxBackoff ? _options.MaxBackoff : delay;
    }

    /// <summary>
    /// A consumer span that links to the trace which raised the event, rather than joining it:
    /// that trace ended long ago, and a redelivery would give it a second child.
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
