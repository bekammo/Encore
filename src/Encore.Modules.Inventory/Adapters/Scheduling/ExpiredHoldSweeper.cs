using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Encore.Modules.Inventory.Adapters.Scheduling;

/// <summary>
/// Flips seats whose holds have lapsed back to available in Postgres, so the
/// table says what every read path already believes.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is cleanup, and nothing may depend on it.</b> A hold that has lapsed is
/// already available to every reader and every writer, because
/// <c>Seat.EffectiveStatusAt</c> says so on every path — the row is merely untidy
/// (<c>DECISIONS.md</c> 007). What this removes is a stale-looking row, an index
/// entry pointing at a hold nobody has, and a <c>SeatReleased(Expired)</c> that
/// would otherwise never be published for a hold no new client ever came along to
/// reclaim. It removes no correctness hole, because there is none to remove:
/// <c>ExpiryWithoutTheSweepTests</c> holds the invariants with this class absent
/// entirely, and 062 records what happens under load with <c>SWEEP_ENABLED=false</c>.
/// </para>
/// <para>
/// <b>It is safe to run in more than one process, and unlike the reconciler it
/// needs nothing to make that true.</b> Two sweeps that pick the same seat both
/// load it, both call <c>ExpireHold</c>, and both try to save — at which point
/// <c>xmin</c> arbitrates, exactly as it does between two clients racing for a
/// hold. The loser is told it lost, writes nothing and publishes nothing, because
/// its outbox row was in the transaction that rolled back. 061's worry about
/// double-sweeping is specific to <c>PaymentReconciler</c>, whose expensive half is
/// a call to a third party that no database token can arbitrate; this job calls
/// nothing. So there is no lease here, and that is an argument rather than an
/// omission.
/// </para>
/// <para>
/// <b><c>BackgroundService</c>, not <c>IHostedLifecycleService</c></b>, for
/// <c>OutboxDispatcher</c>'s reason: the migrators must finish before Kestrel opens
/// the socket, and a tidy-up has no such claim on startup.
/// </para>
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

        while (!stoppingToken.IsCancellationRequested)
        {
            int visited;

            try
            {
                visited = await SweepBatchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The loop must outlive anything one sweep can throw, for the reason
                // the dispatcher gives: ending a BackgroundService silently would
                // stop this for the life of the process. Here that is the mildest
                // version of the failure in this codebase — the design survives this
                // never running at all — but a job that quietly died would still be
                // a job nobody could tell was dead.
                _logger.LogError(ex, "Expired-hold sweep failed. Retrying after {PollInterval}.", _options.PollInterval);
                visited = 0;
            }

            // A full batch means more is waiting, so go straight round again — the
            // dispatcher's rule, and deliberately not the reconciler's. The
            // reconciler must sleep on a full batch because a row it failed to
            // resolve is still first in the next query, so looping would hammer a
            // third party with the same unanswerable question. Every row this
            // visits is settled by the visit, so looping makes definite progress
            // and a backlog drains rather than trickling out at a batch a minute.
            if (visited >= _options.BatchSize)
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

        _logger.LogInformation("Inventory expired-hold sweep stopped.");
    }

    /// <summary>Sweeps one batch of lapsed holds.</summary>
    /// <returns>How many candidates were visited, expired or not.</returns>
    /// <remarks>
    /// <para>
    /// Internal rather than private so the integration tests can drive one sweep
    /// deterministically, for the reason <c>OutboxDispatcher.DispatchBatchAsync</c>
    /// gives: a test that started the hosted service and waited would be timing
    /// dependent, and its failures would be indistinguishable from the bug it
    /// exists to catch.
    /// </para>
    /// <para>
    /// <b>Candidates first, then one scope per seat</b> — <c>PaymentReconciler</c>'s
    /// shape, and for its reason. One transaction over the whole batch would mean a
    /// single seat losing its race on <c>xmin</c> rolls back every tidy-up beside it
    /// and poisons the change tracker for the rest. A scope each costs more round
    /// trips and buys independence, which is the right trade for work nobody is
    /// waiting on.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// The seats whose holds have lapsed, read in a scope that closes before
    /// anything is written.
    /// </summary>
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
    /// <returns>Whether a lapsed hold was actually ended.</returns>
    /// <remarks>
    /// <b>The instant is the one the candidate query used, not a fresh reading.</b>
    /// A seat named by that query is expired as of that instant and stays expired,
    /// because time does not run backwards and the only thing that can rescue the
    /// row is a new hold — which moves it, and is caught below by <c>xmin</c>.
    /// Re-reading the clock per seat would let the sweep's verdict drift from its
    /// own selection for no gain.
    /// </remarks>
    private async Task<bool> ExpireAsync(Guid seatId, DateTime utcNow, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var seats = scope.ServiceProvider.GetRequiredService<ISeatRepository>();

        var seat = await seats.GetByIdAsync(seatId, cancellationToken).ConfigureAwait(false);

        // Gone between the query and here. Nothing in this module deletes seats
        // today, so this guards against a future that does rather than a case
        // anybody has seen.
        if (seat is null)
        {
            return false;
        }

        // The aggregate re-decides. A seat sold or re-held since the query says no
        // here, writes nothing and raises nothing — which is what keeps the SQL
        // predicate a selection rather than a second copy of the expiry rule.
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
            // Somebody moved the row while this was deciding: another sweep, or a
            // client reclaiming the seat on the lazy path. Either way the outcome
            // this wanted has happened or is about to, and nothing was written — so
            // there is no retry. The three seat handlers retry once because a
            // customer is waiting on the answer; nobody is waiting on this, and the
            // next sweep picks the row up if it still needs picking up.
            _logger.LogDebug(
                "Seat {SeatId} moved while the sweep was expiring it. Leaving it to the next sweep.",
                seatId);
            return false;
        }

        return true;
    }
}
