using Microsoft.Extensions.Logging;

namespace Encore.Modules.Inventory.Adapters.Scheduling;

/// <summary>
/// A zero batch counts as full and spins, and a negative interval throws on the first wait;
/// <c>InventoryModule</c> validates both at startup.
/// </summary>
internal static class PollingLoop
{
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
