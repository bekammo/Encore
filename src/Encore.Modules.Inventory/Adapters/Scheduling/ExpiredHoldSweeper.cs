using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Encore.Modules.Inventory.Adapters.Scheduling;

/// <summary>
/// Cleanup only (006): every read already treats a lapsed hold as available, and the
/// invariants hold with this job off (ExpiryWithoutTheSweepTests). It goes through the
/// aggregate, not a bulk UPDATE, so each expiry still publishes <c>SeatReleased(Expired)</c>.
/// Two sweeps on one seat are settled by <c>xmin</c>.
/// </summary>
internal sealed class ExpiredHoldSweeper(
    IServiceScopeFactory scopeFactory,
    IOptions<ExpiredHoldSweepOptions> options,
    TimeProvider timeProvider,
    ILogger<ExpiredHoldSweeper> logger) : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ExpiredHoldSweepOptions _options = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<ExpiredHoldSweeper> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Inventory expired-hold sweep started: batch {BatchSize}, poll {PollInterval}.",
            _options.BatchSize,
            _options.PollInterval);

        await PollingLoop.RunAsync(
            SweepBatchAsync,
            _options.BatchSize,
            _options.PollInterval,
            _timeProvider,
            _logger,
            "Expired-hold sweep",
            stoppingToken).ConfigureAwait(false);

        _logger.LogInformation("Inventory expired-hold sweep stopped.");
    }

    internal async Task<int> SweepBatchAsync(CancellationToken cancellationToken)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var candidates = await CandidatesAsync(utcNow, cancellationToken).ConfigureAwait(false);

        if (candidates.Count is 0)
        {
            return 0;
        }

        var expired = 0;

        foreach (var seatId in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await ExpireAsync(seatId, utcNow, cancellationToken).ConfigureAwait(false))
            {
                expired++;
            }
        }

        _logger.LogInformation(
            "Expired-hold sweep visited {Visited} seats and expired {Expired} of them.",
            candidates.Count,
            expired);

        // Visits, not expiries: every visit settles its row, so a full batch is safe to follow
        // at once.
        return candidates.Count;
    }

    private async Task<IReadOnlyList<Guid>> CandidatesAsync(
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var seats = scope.ServiceProvider.GetRequiredService<ISeatRepository>();

        return await seats
            .FindExpiredHoldsAsync(utcNow, _options.BatchSize, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> ExpireAsync(Guid seatId, DateTime utcNow, CancellationToken cancellationToken)
    {
        // A scope per seat, so a lost race affects only that seat.
        using var scope = _scopeFactory.CreateScope();
        var seats = scope.ServiceProvider.GetRequiredService<ISeatRepository>();

        var seat = await seats.GetByIdAsync(seatId, cancellationToken).ConfigureAwait(false);

        if (seat is null)
        {
            return false;
        }

        if (!seat.ExpireHold(utcNow))
        {
            return false;
        }

        try
        {
            await seats.SaveAsync(seat, cancellationToken).ConfigureAwait(false);
        }
        catch (ConcurrentSeatModificationException)
        {
            _logger.LogDebug(
                "Seat {SeatId} moved while the sweep was expiring it. Leaving it to the next sweep.",
                seatId);
            return false;
        }

        return true;
    }
}
