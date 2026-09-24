using System.Collections.Concurrent;
using Encore.Modules.Inventory.Adapters.Scheduling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Encore.Modules.Inventory.UnitTests;

public sealed class PollingLoopTests
{
    private const int BatchSize = 10;
    private const string Job = "Test job";

    // Whole milliseconds, which is what Task.Delay hands the clock.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    // How long a step may take before it counts as a hang. Never a sleep.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task AFullBatch_ShouldRunAgainAtOnceWithoutTheClockMoving()
    {
        var clock = new ObservedClock();
        var batches = new ScriptedBatches(Returns(BatchSize), Returns(BatchSize + 1), Returns(BatchSize - 1));
        var logger = new CollectingLogger();
        using var stopping = new CancellationTokenSource();

        var run = Start(batches, clock, logger, stopping.Token);

        await batches.WaitForCallsAsync(3);
        await clock.WaitForTimersAsync(1);

        Assert.Equal(3, batches.Calls);
        Assert.Equal(PollInterval, Assert.Single(clock.DueTimes));
        Assert.Equal(0, clock.Fired);

        await StopAsync(run, stopping);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task AShortBatch_ShouldWaitExactlyOnePollInterval()
    {
        var clock = new ObservedClock();
        var batches = new ScriptedBatches(Returns(BatchSize - 1), Returns(0));
        var logger = new CollectingLogger();
        using var stopping = new CancellationTokenSource();

        var run = Start(batches, clock, logger, stopping.Token);

        await batches.WaitForCallsAsync(1);
        await clock.WaitForTimersAsync(1);

        // A timer fires inside Advance, so a count of zero here is a fact, not a race.
        clock.Advance(PollInterval - TimeSpan.FromTicks(1));
        Assert.Equal(0, clock.Fired);
        Assert.Equal(1, batches.Calls);

        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(1, clock.Fired);

        await batches.WaitForCallsAsync(2);
        await clock.WaitForTimersAsync(2);

        Assert.Equal(2, batches.Calls);
        Assert.Equal([PollInterval, PollInterval], clock.DueTimes);

        await StopAsync(run, stopping);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public Task AFailingBatch_ShouldBeLoggedAndWaitedOut_AndTheLoopShouldCarryOn() =>
        AssertLoggedAndWaitedOutAsync(new InvalidOperationException("The database went away."));

    [Fact]
    public Task ACancelledBatch_WhileNotStopping_ShouldCountAsAFailure() =>
        AssertLoggedAndWaitedOutAsync(new OperationCanceledException("A command timed out."));

    [Fact]
    public async Task Stopping_DuringTheWait_ShouldEndTheLoopWithoutThrowing()
    {
        var clock = new ObservedClock();
        var batches = new ScriptedBatches(Returns(0));
        var logger = new CollectingLogger();
        using var stopping = new CancellationTokenSource();

        var run = Start(batches, clock, logger, stopping.Token);

        await clock.WaitForTimersAsync(1);
        Assert.False(run.IsCompleted);

        await StopAsync(run, stopping);

        Assert.Equal(1, batches.Calls);
        Assert.Equal(0, clock.Fired);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Stopping_DuringABatch_ShouldEndTheLoopWithoutThrowingOrLogging()
    {
        var clock = new ObservedClock();
        var batches = new ScriptedBatches(RunsUntilStopped());
        var logger = new CollectingLogger();
        using var stopping = new CancellationTokenSource();

        var run = Start(batches, clock, logger, stopping.Token);

        await batches.WaitForCallsAsync(1);
        Assert.False(run.IsCompleted);

        await StopAsync(run, stopping);

        Assert.Equal(1, batches.Calls);
        Assert.Empty(clock.DueTimes);
        Assert.Empty(logger.Entries);
    }

    private static async Task AssertLoggedAndWaitedOutAsync(Exception failure)
    {
        var clock = new ObservedClock();
        var batches = new ScriptedBatches(Throws(failure), Returns(0));
        var logger = new CollectingLogger();
        using var stopping = new CancellationTokenSource();

        var run = Start(batches, clock, logger, stopping.Token);

        await batches.WaitForCallsAsync(1);
        await clock.WaitForTimersAsync(1);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(failure, entry.Exception);
        Assert.Contains(Job, entry.Message, StringComparison.Ordinal);
        Assert.Equal(PollInterval, Assert.Single(clock.DueTimes));

        clock.Advance(PollInterval);

        await batches.WaitForCallsAsync(2);
        await clock.WaitForTimersAsync(2);

        Assert.Equal(2, batches.Calls);
        Assert.Single(logger.Entries);

        await StopAsync(run, stopping);
    }

    private static Task Start(
        ScriptedBatches batches,
        ObservedClock clock,
        ILogger logger,
        CancellationToken stoppingToken) =>
        PollingLoop.RunAsync(batches.RunAsync, BatchSize, PollInterval, clock, logger, Job, stoppingToken);

    private static async Task StopAsync(Task run, CancellationTokenSource stopping)
    {
        await stopping.CancelAsync();
        await run.WaitAsync(Patience);
    }

    private static Func<CancellationToken, Task<int>> Returns(int taken) =>
        _ => Task.FromResult(taken);

    private static Func<CancellationToken, Task<int>> Throws(Exception failure) =>
        _ => Task.FromException<int>(failure);

    private static Func<CancellationToken, Task<int>> RunsUntilStopped() =>
        async cancellationToken =>
        {
            var stopped = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            using (cancellationToken.Register(() => stopped.TrySetCanceled(cancellationToken)))
            {
                return await stopped.Task;
            }
        };

    private sealed class ScriptedBatches(params Func<CancellationToken, Task<int>>[] script)
    {
        private readonly SemaphoreSlim _called = new(0);
        private int _calls;
        private int _seen;

        public int Calls => Volatile.Read(ref _calls);

        public Task<int> RunAsync(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            _called.Release();

            return call <= script.Length ? script[call - 1](cancellationToken) : Task.FromResult(0);
        }

        public async Task WaitForCallsAsync(int total)
        {
            while (_seen < total)
            {
                Assert.True(await _called.WaitAsync(Patience), $"Batch {_seen + 1} did not run within {Patience}.");
                _seen++;
            }
        }
    }

    private sealed class ObservedClock : TimeProvider
    {
        private readonly FakeTimeProvider _fake = new();
        private readonly SemaphoreSlim _created = new(0);
        private readonly ConcurrentQueue<TimeSpan> _dueTimes = new();
        private int _fired;
        private int _seen;

        public IReadOnlyCollection<TimeSpan> DueTimes => _dueTimes;

        public int Fired => Volatile.Read(ref _fired);

        public override long TimestampFrequency => _fake.TimestampFrequency;

        public void Advance(TimeSpan delta) => _fake.Advance(delta);

        public override DateTimeOffset GetUtcNow() => _fake.GetUtcNow();

        public override long GetTimestamp() => _fake.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _fake.CreateTimer(
                timerState =>
                {
                    Interlocked.Increment(ref _fired);
                    callback(timerState);
                },
                state,
                dueTime,
                period);

            _dueTimes.Enqueue(dueTime);
            _created.Release();

            return timer;
        }

        public async Task WaitForTimersAsync(int total)
        {
            while (_seen < total)
            {
                Assert.True(await _created.WaitAsync(Patience), $"Timer {_seen + 1} was not created within {Patience}.");
                _seen++;
            }
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class CollectingLogger : ILogger
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
    }
}
