using Encore.Modules.Orders.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Encore.Modules.Orders.Data;

/// <summary>
/// Finishes orders whose seats sold but whose capture never got an answer, by confirming them
/// again on the customer's behalf.
/// </summary>
/// <remarks>
/// The confirm that left an order awaiting capture answered 200, so the customer has no reason
/// to ask again, and an uncaptured authorisation lapses at the gateway with the seats already
/// given out (025). It adds no rule of its own: it runs the confirm a customer would, so with it
/// off the next confirm still finishes the order, and nothing depends on it running. A Postgres
/// advisory lock per sweep keeps two instances from duplicating work; <c>xmin</c> still guards
/// each order.
/// </remarks>
internal sealed class CaptureSweeper(
    IServiceScopeFactory scopeFactory,
    IOptions<CaptureSweepOptions> options,
    TimeProvider timeProvider,
    ILogger<CaptureSweeper> logger) : BackgroundService
{
    /// <summary>This job's key in Postgres's advisory-lock namespace. Arbitrary but unique.</summary>
    internal const long LeaseKey = 3_811_030_058;

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly CaptureSweepOptions _options = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<CaptureSweeper> _logger = logger;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Capture sweep started: batch {BatchSize}, poll {PollInterval}, minimum age {MinimumAge}.",
            _options.BatchSize,
            _options.PollInterval,
            _options.MinimumAge);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepBatchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Capture sweep failed. Retrying after {PollInterval}.", _options.PollInterval);
            }

            // Always sleep, even after a full batch: a capture that went unanswered once is
            // still first in line, and asking a gateway faster does not make it answer.
            try
            {
                await Task.Delay(_options.PollInterval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Capture sweep stopped.");
    }

    /// <summary>
    /// Confirms one batch of owed orders under a transaction-scoped advisory lock. Another
    /// instance holding it means this batch is redundant. Internal so tests can drive one sweep.
    /// </summary>
    /// <returns>How many orders ended confirmed.</returns>
    internal async Task<int> SweepBatchAsync(CancellationToken cancellationToken)
    {
        using var leaseScope = _scopeFactory.CreateScope();
        var leaseContext = leaseScope.ServiceProvider.GetRequiredService<OrdersDbContext>();

        await using var lease = await leaseContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var acquired = await leaseContext.Database
            .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({LeaseKey}) AS \"Value\"")
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!acquired)
        {
            _logger.LogDebug("Capture sweep skipped: another instance already holds the lease.");
            return 0;
        }

        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - _options.MinimumAge;

        var owed = await leaseContext.Orders
            .AsNoTracking()
            .Where(order => order.Status == OrderStatus.AwaitingCapture && order.SoldAt <= cutoff)
            .OrderBy(order => order.SoldAt)
            .Take(_options.BatchSize)
            .Select(order => new { order.Id, order.ClientId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var confirmed = 0;

        foreach (var order in owed)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Its own scope, so one order's lost race does not reach the next.
            using var scope = _scopeFactory.CreateScope();

            var result = await scope.ServiceProvider
                .GetRequiredService<CheckoutService>()
                .ConfirmAsync(order.ClientId, order.Id, cancellationToken)
                .ConfigureAwait(false);

            if (result.Order?.Status is OrderStatus.Confirmed)
            {
                confirmed++;
                _logger.LogInformation("Order {OrderId} captured by the sweep and confirmed.", order.Id);
            }
            else if (result.Order?.Status is OrderStatus.Failed)
            {
                _logger.LogWarning(
                    "Order {OrderId} has its seats but its authorisation is gone; it needs to be looked at.",
                    order.Id);
            }
        }

        return confirmed;
    }
}
