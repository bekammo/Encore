using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Encore.Modules.Inventory.Adapters.Scheduling;

/// <summary>
/// Flips lapsed holds back to available in Postgres, so the table matches what every
/// read path already treats as true.
/// </summary>
/// <remarks>
/// Cleanup only: lapsed holds are already available on every path, and the invariants
/// hold with this job disabled. It goes through the aggregate so each expiry still
/// publishes <c>SeatReleased(Expired)</c>. Safe to run in several processes: two sweeps
/// on one seat are settled by <c>xmin</c>.
/// </remarks>
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

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Inventory expired-hold sweep started: batch {BatchSize}, poll {PollInterval}.",
            _options.BatchSize,
            _options.PollInterval);

        // Counts visits, not expiries: every visit settles its row, so a full batch of
        // visits is safe to follow straight away.
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

    /// <summary>
    /// Sweeps one batch, one scope per seat so a lost race affects only that seat.
    /// Internal so tests can drive one sweep.
    /// </summary>
    /// <returns>How many candidates were visited.</returns>
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

    /// <summary>Loads one candidate and lets the aggregate decide.</summary>
    /// <returns>Whether a lapsed hold was ended.</returns>
    private async Task<bool> ExpireAsync(Guid seatId, DateTime utcNow, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var seats = scope.ServiceProvider.GetRequiredService<ISeatRepository>();

        var seat = await seats.GetByIdAsync(seatId, cancellationToken).ConfigureAwait(false);

        if (seat is null)
        {
            return false;
        }

        // A seat sold or re-held since the query refuses here and nothing is written.
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
            // Another sweep or a client got there first. No retry: nobody is waiting on this.
            _logger.LogDebug(
                "Seat {SeatId} moved while the sweep was expiring it. Leaving it to the next sweep.",
                seatId);
            return false;
        }

        return true;
    }
}
