using Microsoft.Extensions.Logging;

namespace Encore.Modules.Inventory.Adapters.Caching;

/// <summary>
/// Warns once per outage, not per attempt: logging every refused attempt flooded the log with
/// thousands of stack traces a second.
/// </summary>
internal sealed class LockOutageLog(ILogger logger)
{
    private readonly ILogger _logger = logger;

    private long _refusedSinceLastAnswer;

    public void Refused(string operation, string resource, Exception exception)
    {
        if (Interlocked.Increment(ref _refusedSinceLastAnswer) == 1)
        {
            _logger.LogWarning(
                exception,
                "Redis lock unavailable: {Operation} for {Resource} failed ({ExceptionType}). "
                + "Further refusals log at Debug until Redis answers again.",
                operation,
                resource,
                exception.GetType().Name);

            return;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Redis lock still unavailable: {Operation} for {Resource} failed ({ExceptionType}).",
                operation,
                resource,
                exception.GetType().Name);
        }
    }

    public void Answered()
    {
        if (Volatile.Read(ref _refusedSinceLastAnswer) == 0)
        {
            return;
        }

        var refused = Interlocked.Exchange(ref _refusedSinceLastAnswer, 0);

        // Only one of two racing answers gets the count.
        if (refused > 0)
        {
            _logger.LogInformation(
                "Redis lock answering again after {Refused} refused attempts.",
                refused);
        }
    }
}
