using Microsoft.Extensions.Logging;

namespace Encore.Modules.Orders.Data;

/// <summary>The poll loop both of Orders' sweeps run.</summary>
internal static class SweepLoop
{
    /// <summary>
    /// Always sleeps, even after a full batch: an order the gateway did not answer for is still
    /// first in line, and asking faster does not make it answer. A failed pass is logged and
    /// retried after the interval, so one bad batch never stops the sweep.
    /// </summary>
    public static async Task RunAsync(
        string name,
        Func<CancellationToken, Task<int>> sweep,
        TimeSpan pollInterval,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await sweep(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Sweep} failed. Retrying after {PollInterval}.", name, pollInterval);
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
