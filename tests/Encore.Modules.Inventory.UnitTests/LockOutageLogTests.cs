using Encore.Modules.Inventory.Adapters.Caching;
using Microsoft.Extensions.Logging;

namespace Encore.Modules.Inventory.UnitTests;

/// <summary>
/// The Redis lock logs an outage at its edges: one warning when it starts, one line when it
/// ends, nothing at the default level in between.
/// </summary>
public class LockOutageLogTests
{
    /// <summary>Any exception will do; which types count as an outage is the adapter's call.</summary>
    private static readonly Exception Down = new TimeoutException("No connection is active/available.");

    [Fact]
    public void Refused_TheFirstTime_ShouldWarnWithTheException()
    {
        var logger = new CollectingLogger();
        var outage = new LockOutageLog(logger);

        outage.Refused("acquire", "client:1", Down);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Same(Down, entry.Exception);
        Assert.Contains(nameof(TimeoutException), entry.Message, StringComparison.Ordinal);
    }

    /// <summary>Thousands of refusals produce one warning.</summary>
    [Fact]
    public void Refused_Repeatedly_ShouldWarnOnceAndLogTheRestAtDebugWithoutTheException()
    {
        var logger = new CollectingLogger();
        var outage = new LockOutageLog(logger);

        for (var i = 0; i < 100; i++)
        {
            outage.Refused("acquire", $"client:{i}", Down);
        }

        Assert.Single(logger.Entries, entry => entry.Level is LogLevel.Warning);
        Assert.Equal(99, logger.Entries.Count(entry => entry.Level is LogLevel.Debug));
        Assert.All(
            logger.Entries.Where(entry => entry.Level is LogLevel.Debug),
            entry =>
            {
                Assert.Null(entry.Exception);
                Assert.Contains(nameof(TimeoutException), entry.Message, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void Refused_WhenDebugIsOff_ShouldWriteNothingAfterTheWarning()
    {
        var logger = new CollectingLogger { MinimumLevel = LogLevel.Information };
        var outage = new LockOutageLog(logger);

        for (var i = 0; i < 100; i++)
        {
            outage.Refused("acquire", "client:1", Down);
        }

        Assert.Single(logger.Entries);
    }

    [Fact]
    public void Answered_AfterAnOutage_ShouldSayHowManyAttemptsWereRefused()
    {
        var logger = new CollectingLogger { MinimumLevel = LogLevel.Information };
        var outage = new LockOutageLog(logger);

        for (var i = 0; i < 7; i++)
        {
            outage.Refused("acquire", "client:1", Down);
        }

        outage.Answered();

        var recovered = Assert.Single(logger.Entries, entry => entry.Level is LogLevel.Information);
        Assert.Contains("7", recovered.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Answered_WhenNothingWasRefused_ShouldSayNothing()
    {
        var logger = new CollectingLogger();
        var outage = new LockOutageLog(logger);

        outage.Answered();
        outage.Answered();

        Assert.Empty(logger.Entries);
    }

    /// <summary>A second outage gets its own warning and its own count.</summary>
    [Fact]
    public void Refused_AfterRecovering_ShouldWarnAgain()
    {
        var logger = new CollectingLogger { MinimumLevel = LogLevel.Information };
        var outage = new LockOutageLog(logger);

        outage.Refused("acquire", "client:1", Down);
        outage.Refused("acquire", "client:1", Down);
        outage.Answered();
        outage.Refused("release", "client:1", Down);
        outage.Answered();

        Assert.Equal(
            [LogLevel.Warning, LogLevel.Information, LogLevel.Warning, LogLevel.Information],
            logger.Entries.Select(entry => entry.Level));
        Assert.Contains("2", logger.Entries[1].Message, StringComparison.Ordinal);
        Assert.Contains("1", logger.Entries[3].Message, StringComparison.Ordinal);
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class CollectingLogger : ILogger
    {
        public LogLevel MinimumLevel { get; init; } = LogLevel.Trace;

        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= MinimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
            }
        }
    }
}
