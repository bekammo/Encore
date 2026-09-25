using Encore.Modules.Orders.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Encore.Modules.Orders.Data;

/// <summary>
/// Ends <c>Pending</c> orders whose holds lapsed: seats released, then the authorisation voided,
/// then the order expired (031). The recorded expiry only picks candidates; each seat's answer
/// comes from Inventory. The advisory lock only stops two instances duplicating work;
/// <c>xmin</c> guards each order.
/// </summary>
internal sealed class OrderExpirySweeper(
    IServiceScopeFactory scopeFactory,
    IOptions<OrderExpirySweepOptions> options,
    TimeProvider timeProvider,
    ILogger<OrderExpirySweeper> logger) : BackgroundService
{
    /// <summary>Arbitrary, but no other job's advisory lock in the same database may use it.</summary>
    internal const long LeaseKey = 3_811_030_059;

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly OrderExpirySweepOptions _options = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<OrderExpirySweeper> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Order expiry sweep started: batch {BatchSize}, poll {PollInterval}, grace {Grace}.",
            _options.BatchSize,
            _options.PollInterval,
            _options.Grace);

        await SweepLoop.RunAsync(
                "Order expiry sweep",
                SweepBatchAsync,
                _options.PollInterval,
                _timeProvider,
                _logger,
                stoppingToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Order expiry sweep stopped.");
    }

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
            _logger.LogDebug("Order expiry sweep skipped: another instance already holds the lease.");
            return 0;
        }

        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - _options.Grace;

        var lapsed = await leaseContext.Orders
            .AsNoTracking()
            .Where(order => order.Status == OrderStatus.Pending && order.HoldsExpireAt <= cutoff)
            .OrderBy(order => order.HoldsExpireAt)
            .Take(_options.BatchSize)
            .Select(order => new { order.Id, order.ClientId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var expired = 0;

        foreach (var order in lapsed)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Its own scope, so one order's lost race does not reach the next.
            using var scope = _scopeFactory.CreateScope();

            var result = await scope.ServiceProvider
                .GetRequiredService<CheckoutService>()
                .ExpireAsync(order.ClientId, order.Id)
                .ConfigureAwait(false);

            switch (result.Order?.Status)
            {
                case OrderStatus.Expired:
                    expired++;
                    _logger.LogInformation("Order {OrderId} expired by the sweep; its seats and money are released.", order.Id);
                    break;

                case OrderStatus.AwaitingCapture or OrderStatus.Confirmed:
                    _logger.LogInformation("Order {OrderId} had sold before it was recorded; the sweep finished its confirm.", order.Id);
                    break;

                case OrderStatus.PaymentDue:
                    _logger.LogWarning(
                        "Order {OrderId} had sold before it was recorded, and the gateway refused its capture; the customer owes the payment.",
                        order.Id);
                    break;
            }
        }

        return expired;
    }
}
