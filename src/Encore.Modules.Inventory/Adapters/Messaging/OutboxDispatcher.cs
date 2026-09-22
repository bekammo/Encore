using System.Diagnostics;
using Encore.Modules.Inventory.Adapters.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Encore.Modules.Inventory.Adapters.Messaging;

/// <summary>
/// Reads undelivered rows out of <c>inventory.outbox_messages</c> and hands them to
/// their handlers, retrying with a backoff and giving up into a dead letter.
/// </summary>
/// <remarks>
/// <para>
/// <b>Delivery is at-least-once, and no seat invariant depends on this class.</b>
/// The drain has already committed the event alongside the state change; all that is
/// left is getting it out, and getting it out late is a delay rather than a
/// correctness failure. This is 007's rule about the expiry sweep applied to
/// publication: if a test cannot pass with the dispatcher disabled, the dispatcher
/// has become load-bearing and the design is broken.
/// </para>
/// <para>
/// <b><c>BackgroundService</c>, not <c>IHostedLifecycleService</c>.</b> The four
/// migrators use the lifecycle interface because they must finish before Kestrel
/// opens the socket. Nothing here has that requirement — a message that waits a
/// second while the host finishes starting has lost nothing — and running during
/// <c>StartingAsync</c> would block startup on a loop that never ends.
/// </para>
/// <para>
/// <b>What the ordering guarantee is, precisely.</b> Rows are claimed and delivered
/// in <c>Id</c> order, so the events raised by one transaction always reach handlers
/// in the order the aggregate raised them — which is the property 007 needs, since a
/// reclaim's <c>SeatReleased</c> and <c>SeatHeld</c> are written by one save. Across
/// transactions there is no such promise: ids are assigned at INSERT and commits
/// interleave, and a failing message lets later ones overtake it while it backs off.
/// Blocking the queue behind a poison message would preserve a global order nothing
/// asked for, at the price of one bad row stopping every good one.
/// </para>
/// </remarks>
internal sealed class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    OutboxEventCatalog catalog,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    /// <summary>
    /// The claim query. Raw SQL because EF Core cannot express a locking clause, and
    /// the locking clause is the entire point of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>FOR UPDATE SKIP LOCKED</c> is what makes more than one instance safe.</b>
    /// Rows another dispatcher already holds are passed over rather than waited on, so
    /// two hosts drain the same table in parallel and neither delivers the other's
    /// message. There is one host today; this is the cheap half of the cloud deploy On
    /// Tour will want, and a poll that could not be run twice would have to be
    /// rewritten then.
    /// </para>
    /// <para>
    /// <b>The claim is on <c>ProcessedAt IS NULL</c>, never on a high-water mark.</b>
    /// A dispatcher tracking "the last id I saw" would skip rows committed after it by
    /// a transaction that started before it, because ids are assigned at INSERT and
    /// commits do not follow suit. Filtering on the column the delivery itself writes
    /// is immune to that.
    /// </para>
    /// <para>
    /// A compile-time constant, so the schema cannot drift from
    /// <see cref="InventoryPersistence.Schema"/> and no user input reaches the string.
    /// It is a <c>$$</c> raw string, so <c>{{...}}</c> interpolates and a single brace is
    /// literal — which is what lets <c>{0}</c>, <c>{1}</c> and <c>{2}</c> reach Npgsql as
    /// the placeholders <c>FromSqlRaw</c> binds as parameters.
    /// </para>
    /// </remarks>
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
                // The loop must outlive anything one tick can throw — a database that
                // went away, a claim that deadlocked. Not catching here would end the
                // BackgroundService silently and stop delivery for the life of the
                // process, which is the one outcome worse than a slow outbox.
                _logger.LogError(ex, "Outbox tick failed. Retrying after {PollInterval}.", _options.PollInterval);
                claimed = 0;
            }

            // A full batch means there is very likely more waiting, so go straight
            // round again: a backlog drains at the speed of the database rather than
            // one batch per poll interval. That is what keeps a one-second poll from
            // meaning one-second delivery latency under load.
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

    /// <summary>Claims one batch, delivers it, and records what happened.</summary>
    /// <returns>How many messages were claimed.</returns>
    /// <remarks>
    /// Internal rather than private so the integration tests can drive one tick
    /// deterministically. A test that started the hosted service and waited would be
    /// timing-dependent, and a flaky test about an at-least-once mechanism is worse
    /// than no test, because the failure it eventually reports is indistinguishable
    /// from the thing it is meant to catch.
    /// </remarks>
    internal async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        // The claim and the marking share a transaction, so the row lock FOR UPDATE
        // takes is still held when the outcome is written. Without it another
        // dispatcher could claim the same row in between.
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

        // Wall clock, not TimeProvider: this bounds how long a real transaction
        // holds real row locks, and a test with a fake clock still wants that bound
        // to be about the time the test actually spends. DECISIONS 069.
        var started = Stopwatch.StartNew();
        var delivered = 0;

        foreach (var message in claimed)
        {
            if (started.Elapsed >= _options.MaxBatchDuration)
            {
                // Out of budget. Everything after this message is untouched — no
                // attempt recorded, no backoff applied — so committing now simply
                // hands it back, and the next tick claims it again. Stopping is
                // cheaper than the alternative, which is a transaction that stays
                // open for as long as the slowest consumer feels like taking.
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

    /// <summary>Hands one message to its handler, under a deadline.</summary>
    /// <remarks>
    /// <para>
    /// <b>The handler gets its own token, and a handler that overruns it fails like
    /// any other handler.</b> The alternative — the one this had until 069 — is that
    /// a consumer blocked on a lock, a slow query or an unreachable dependency holds
    /// this tick's transaction open for as long as it likes, with the batch's rows
    /// locked the whole time.
    /// </para>
    /// <para>
    /// The linked source means shutdown still cancels immediately and is still told
    /// apart below: the caller's token being the one that fired is what separates
    /// "we are stopping" from "this consumer is too slow", and only the first leaves
    /// the row untouched.
    /// </para>
    /// </remarks>
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
            // Shutting down. Leave the row exactly as it was: it was never delivered,
            // and counting a cancelled attempt against its budget would spend a
            // message's retries on restarts rather than on failures.
            throw;
        }
        catch (Exception ex)
        {
            // An overrun arrives here as an OperationCanceledException whose token is
            // the deadline's rather than the caller's, and it is meant to: a handler
            // that ran out of time failed, and the row backs off and is retried.
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

    /// <summary>Exponential, capped. The first retry waits the base delay, then it doubles.</summary>
    /// <remarks>
    /// Shifting rather than <c>Math.Pow</c>, and the exponent is clamped before the
    /// shift rather than the result clamped after it — a large attempt count would
    /// overflow the shift long before <see cref="OutboxOptions.MaxBackoff"/> caught
    /// the value.
    /// </remarks>
    private TimeSpan BackoffFor(int attemptsSoFar)
    {
        var exponent = Math.Min(attemptsSoFar, 16);
        var delay = _options.BaseBackoff * (1L << exponent);

        return delay > _options.MaxBackoff ? _options.MaxBackoff : delay;
    }
}
