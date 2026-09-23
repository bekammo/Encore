using Microsoft.Extensions.Logging;

namespace Encore.Modules.Inventory.Adapters.Caching;

/// <summary>
/// What <see cref="RedisDistributedLock"/> says about Redis being unavailable:
/// once when it stops answering, once when it starts again, and quietly in between.
/// </summary>
/// <remarks>
/// <para>
/// <b>Transitions, not attempts.</b> 074 made every refused attempt log a warning
/// with its exception, because the exception's type was the one piece of evidence
/// that could name the mechanism 073 was chasing. 079 named it, and in doing so
/// counted 95,244 of those warnings in a 30-second outage, about 3,000 a second,
/// each with a stack trace. That is the prime suspect for why a purchase, which
/// takes no lock at all, doubled its median while Redis was gone. The evidence had
/// become the cost.
/// </para>
/// <para>
/// The first refusal after a success still logs a warning with the whole
/// exception, so the type 074 wanted stays in the log. Later refusals log at
/// Debug, carrying the type name but no exception object, so there is no stack
/// trace to format and nothing is written at the default level. The first
/// success after a run of refusals logs how many there were, which is how long
/// the outage was, in the unit that matters here.
/// </para>
/// <para>
/// <b>Counted from what the adapter saw, not from the multiplexer's events.</b>
/// <c>ConnectionFailed</c> and <c>ConnectionRestored</c> describe the socket.
/// This describes whether the lock could answer, which is what a hold actually
/// experienced. It also needs no subscription with a lifetime to manage.
/// </para>
/// <para>
/// The counter is shared by every request, so a success reads it before writing
/// to it: the ordinary case, Redis answering, costs one volatile read and no
/// write to a contended cache line.
/// </para>
/// </remarks>
internal sealed class LockOutageLog(ILogger logger)
{
    private readonly ILogger _logger = logger;

    private long _refusedSinceLastAnswer;

    /// <summary>Records that Redis could not answer this attempt.</summary>
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

    /// <summary>Records that Redis answered, whatever it answered.</summary>
    public void Answered()
    {
        if (Volatile.Read(ref _refusedSinceLastAnswer) == 0)
        {
            return;
        }

        var refused = Interlocked.Exchange(ref _refusedSinceLastAnswer, 0);

        // Two answers racing past the read above both reach the exchange, and only
        // one of them gets the count.
        if (refused > 0)
        {
            _logger.LogInformation(
                "Redis lock answering again after {Refused} refused attempts.",
                refused);
        }
    }
}
