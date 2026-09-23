using Microsoft.Extensions.Logging;

namespace Encore.Modules.Inventory.Adapters.Scheduling;

/// <summary>
/// The loop Inventory's background jobs share: run one batch, go again at once when it came
/// back full, otherwise wait one poll interval. Its settings are checked when the host starts
/// (<c>InventoryModule</c>): a zero batch counts as full and spins, and a negative interval
/// throws on the first wait.
/// </summary>
internal static class PollingLoop
{
    /// <summary>Runs until <paramref name="stoppingToken"/> fires.</summary>
    /// <param name="runBatchAsync">One batch; returns how many items it took on.</param>
    /// <param name="job">The job's name in the failure log.</param>
    public static async Task RunAsync(
        Func<CancellationToken, Task<int>> runBatchAsync,
        int batchSize,
        TimeSpan pollInterval,
        TimeProvider timeProvider,
        ILogger logger,
        string job,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int taken;

            try
            {
                taken = await runBatchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // An exception escaping ExecuteAsync would stop the job for the life of the process.
                logger.LogError(ex, "{Job} failed. Retrying after {PollInterval}.", job, pollInterval);
                taken = 0;
            }

            // A full batch means more is probably waiting.
            if (taken >= batchSize)
            {
                continue;
            }

            try
            {
                await Task.Delay(pollInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
