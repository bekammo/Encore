using Encore.Modules.Payments.Simulation;
using Microsoft.Extensions.Options;

namespace Encore.Modules.Payments.IntegrationTests;

/// <summary>The gateway's memory is a table, emptied before each test because tests reuse keys.</summary>
public sealed class SimulatedPaymentGatewayTests(PaymentsDatabase database)
    : IClassFixture<PaymentsDatabase>, IAsyncLifetime
{
    private const decimal Amount = 99.99m;
    private const string Currency = "GBP";

    private readonly PaymentsDatabase _database = database;

    public Task InitializeAsync() => _database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // -- Outcomes ---------------------------------------------------------

    [Fact]
    public async Task Authorize_WhenNothingIsSetToFail_ShouldSucceedWithAReference()
    {
        var (outcome, reference) = await Gateway().AuthorizeAsync("key-1", Amount, Currency);

        Assert.Equal(GatewayOutcome.Succeeded, outcome);
        Assert.NotNull(reference);
    }

    [Fact]
    public async Task Authorize_WhenAlwaysDeclining_ShouldDeclineWithNoReference()
    {
        var (outcome, reference) = await Gateway(declineRate: 1).AuthorizeAsync("key-1", Amount, Currency);

        Assert.Equal(GatewayOutcome.Declined, outcome);
        Assert.Null(reference);
    }

    [Fact]
    public async Task Authorize_WhenAlwaysTimingOut_ShouldTimeOutEvenIfAlsoAlwaysDeclining()
    {
        var (outcome, reference) = await Gateway(declineRate: 1, timeoutRate: 1)
            .AuthorizeAsync("key-1", Amount, Currency);

        Assert.Equal(GatewayOutcome.TimedOut, outcome);
        Assert.Null(reference);
    }

    [Fact]
    public async Task CaptureAndVoid_WhenAlwaysDeclining_ShouldStillSucceed()
    {
        var gateway = Gateway(declineRate: 1);

        Assert.Equal(GatewayOutcome.Succeeded, await gateway.CaptureAsync("auth_1"));
        Assert.Equal(GatewayOutcome.Succeeded, await gateway.VoidAsync("auth_1"));
    }

    [Fact]
    public async Task CaptureAndVoid_WhenAlwaysTimingOut_ShouldTimeOut()
    {
        var gateway = Gateway(timeoutRate: 1);

        Assert.Equal(GatewayOutcome.TimedOut, await gateway.CaptureAsync("auth_1"));
        Assert.Equal(GatewayOutcome.TimedOut, await gateway.VoidAsync("auth_1"));
    }

    [Fact]
    public async Task Capture_WhenAlwaysRefusingCaptures_ShouldDeclineItButStillVoid()
    {
        var gateway = Gateway(captureDeclineRate: 1);

        Assert.Equal(GatewayOutcome.Declined, await gateway.CaptureAsync("auth_1"));
        Assert.Equal(GatewayOutcome.Succeeded, await gateway.VoidAsync("auth_1"));
    }

    /// <summary>An unanswered capture has no answer to refuse with.</summary>
    [Fact]
    public async Task Capture_WhenAlwaysTimingOut_ShouldTimeOutEvenIfAlsoAlwaysRefusing()
    {
        var gateway = Gateway(timeoutRate: 1, captureDeclineRate: 1);

        Assert.Equal(GatewayOutcome.TimedOut, await gateway.CaptureAsync("auth_1"));
    }

    // -- Idempotency ------------------------------------------------------

    /// <summary>At a 50% decline rate, fifty agreeing answers cannot be luck: the key decides.</summary>
    [Fact]
    public async Task Authorize_WithTheSameKey_ShouldAlwaysGiveTheSameAnswer()
    {
        var gateway = Gateway(declineRate: 0.5);

        var outcomes = new List<GatewayOutcome>();

        for (var attempt = 0; attempt < 50; attempt++)
        {
            var (outcome, _) = await gateway.AuthorizeAsync("one-key", Amount, Currency);
            outcomes.Add(outcome);
        }

        Assert.Single(outcomes.Distinct());
    }

    [Fact]
    public async Task Authorize_WithTheSameKey_ShouldGiveTheSameReference()
    {
        var gateway = Gateway();

        var (_, first) = await gateway.AuthorizeAsync("one-key", Amount, Currency);
        var (_, again) = await gateway.AuthorizeAsync("one-key", Amount, Currency);

        Assert.Equal(first, again);
    }

    [Fact]
    public async Task Authorize_WithDifferentKeys_ShouldGiveDifferentReferences()
    {
        var gateway = Gateway();

        var (_, first) = await gateway.AuthorizeAsync("key-1", Amount, Currency);
        var (_, second) = await gateway.AuthorizeAsync("key-2", Amount, Currency);

        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Authorize_WithoutAKey_ShouldThrow(string key) =>
        await Assert.ThrowsAsync<ArgumentException>(
            () => Gateway().AuthorizeAsync(key, Amount, Currency));

    // -- Surviving the process --------------------------------------------

    [Fact]
    public async Task LookUp_FromAnInstanceThatNeverAuthorised_ShouldStillReportTheHold()
    {
        var authorising = Gateway();
        var (outcome, reference) = await authorising.AuthorizeAsync("key-1", Amount, Currency);
        Assert.Equal(GatewayOutcome.Succeeded, outcome);

        var restarted = Gateway();

        var (record, found) = await restarted.LookUpAsync("key-1");

        Assert.Equal(GatewayRecord.Authorized, record);
        Assert.Equal(reference, found);
    }

    [Fact]
    public async Task Authorize_FromAnotherInstance_ShouldRepeatTheRecordedAnswer()
    {
        var (outcome, _) = await Gateway(declineRate: 1).AuthorizeAsync("key-1", Amount, Currency);
        Assert.Equal(GatewayOutcome.Declined, outcome);

        // Opposite conditions: without the ledger this would roll again and succeed.
        var (again, _) = await Gateway(declineRate: 0).AuthorizeAsync("key-1", Amount, Currency);

        Assert.Equal(GatewayOutcome.Declined, again);
    }

    [Fact]
    public async Task Authorize_WhenTheRequestWasLost_ShouldLeaveNothingForAnotherInstanceToFind()
    {
        await Gateway(timeoutRate: 1, lostRequestRate: 1).AuthorizeAsync("key-1", Amount, Currency);

        var (record, _) = await Gateway().LookUpAsync("key-1");

        Assert.Equal(GatewayRecord.NotFound, record);
    }

    [Fact]
    public async Task Authorize_ConcurrentlyUnderOneKey_ShouldAgreeOnOneAnswer()
    {
        var gateway = Gateway(declineRate: 0.5);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => gateway.AuthorizeAsync("one-key", Amount, Currency)));

        Assert.Single(attempts.Select(attempt => attempt.Outcome).Distinct());
    }

    // -- Reproducibility --------------------------------------------------

    /// <summary>The ledger is emptied between runs, so the replay comes from the seed.</summary>
    [Fact]
    public async Task Authorize_WithTheSameSeed_ShouldProduceTheSameSequence()
    {
        var first = await SequenceAsync(Gateway(declineRate: 0.3, timeoutRate: 0.3, seed: 1234));

        await _database.ResetAsync();

        var again = await SequenceAsync(Gateway(declineRate: 0.3, timeoutRate: 0.3, seed: 1234));

        Assert.Equal(first, again);
    }

    [Fact]
    public async Task Authorize_WithoutASeed_ShouldNotProduceTheSameSequence()
    {
        var first = await SequenceAsync(Gateway(declineRate: 0.3, timeoutRate: 0.3));

        await _database.ResetAsync();

        var again = await SequenceAsync(Gateway(declineRate: 0.3, timeoutRate: 0.3));

        Assert.NotEqual(first, again);
    }

    // -- Latency ----------------------------------------------------------

    [Fact]
    public async Task Authorize_WhenMaxLatencyIsBelowMin_ShouldNotThrow()
    {
        var gateway = new SimulatedPaymentGateway(
            _database.Scopes,
            Options.Create(new PaymentSimulationOptions
            {
                MinLatency = TimeSpan.FromMilliseconds(1),
                MaxLatency = TimeSpan.Zero
            }),
            TimeProvider.System);

        var (outcome, _) = await gateway.AuthorizeAsync("key-1", Amount, Currency);

        Assert.Equal(GatewayOutcome.Succeeded, outcome);
    }

    // -- Lookup -----------------------------------------------------------

    [Fact]
    public async Task LookUp_AfterAnAuthorisationThatArrived_ShouldReportTheHold()
    {
        var (gateway, options) = Configured(timeoutRate: 1, lostRequestRate: 0);

        var (outcome, _) = await gateway.AuthorizeAsync("key-1", Amount, Currency);
        Assert.Equal(GatewayOutcome.TimedOut, outcome);

        options.TimeoutRate = 0;
        var (record, reference) = await gateway.LookUpAsync("key-1");

        Assert.Equal(GatewayRecord.Authorized, record);
        Assert.NotNull(reference);
    }

    [Fact]
    public async Task LookUp_AfterAnAuthorisationThatNeverArrived_ShouldReportNotFound()
    {
        var (gateway, options) = Configured(timeoutRate: 1, lostRequestRate: 1);

        await gateway.AuthorizeAsync("key-1", Amount, Currency);

        options.TimeoutRate = 0;
        var (record, reference) = await gateway.LookUpAsync("key-1");

        Assert.Equal(GatewayRecord.NotFound, record);
        Assert.Null(reference);
    }

    [Fact]
    public async Task LookUp_AfterADeclineThatWasNotHeard_ShouldReportTheDecline()
    {
        var (gateway, options) = Configured(declineRate: 1, timeoutRate: 1, lostRequestRate: 0);

        await gateway.AuthorizeAsync("key-1", Amount, Currency);

        options.TimeoutRate = 0;
        var (record, reference) = await gateway.LookUpAsync("key-1");

        Assert.Equal(GatewayRecord.Declined, record);
        Assert.Null(reference);
    }

    [Fact]
    public async Task LookUp_WhenTheCallerDidHearTheAnswer_ShouldAgreeWithIt()
    {
        var gateway = Gateway();

        var (_, authorised) = await gateway.AuthorizeAsync("key-1", Amount, Currency);
        var (record, reference) = await gateway.LookUpAsync("key-1");

        Assert.Equal(GatewayRecord.Authorized, record);
        Assert.Equal(authorised, reference);
    }

    [Fact]
    public async Task LookUp_WhenTheKeyWasNeverSeen_ShouldReportNotFound()
    {
        var (record, reference) = await Gateway().LookUpAsync("never-asked");

        Assert.Equal(GatewayRecord.NotFound, record);
        Assert.Null(reference);
    }

    [Fact]
    public async Task LookUp_WhenTheGatewayDoesNotAnswer_ShouldReportUnknownRatherThanNotFound()
    {
        var (record, reference) = await Gateway(timeoutRate: 1).LookUpAsync("key-1");

        Assert.Equal(GatewayRecord.Unknown, record);
        Assert.Null(reference);
    }

    [Fact]
    public async Task LookUp_ShouldDecideNothing()
    {
        var gateway = Gateway(declineRate: 1);

        var (before, _) = await gateway.LookUpAsync("key-1");
        Assert.Equal(GatewayRecord.NotFound, before);

        var (outcome, _) = await gateway.AuthorizeAsync("key-1", Amount, Currency);
        Assert.Equal(GatewayOutcome.Declined, outcome);

        var (after, _) = await gateway.LookUpAsync("key-1");
        Assert.Equal(GatewayRecord.Declined, after);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LookUp_WithoutAKey_ShouldThrow(string key) =>
        await Assert.ThrowsAsync<ArgumentException>(() => Gateway().LookUpAsync(key));

    // -- Helpers ----------------------------------------------------------

    private SimulatedPaymentGateway Gateway(
        double declineRate = 0,
        double timeoutRate = 0,
        double lostRequestRate = 0.5,
        int? seed = null,
        double captureDeclineRate = 0) =>
        Configured(declineRate, timeoutRate, lostRequestRate, seed, captureDeclineRate).Gateway;

    private (SimulatedPaymentGateway Gateway, PaymentSimulationOptions Options) Configured(
        double declineRate = 0,
        double timeoutRate = 0,
        double lostRequestRate = 0.5,
        int? seed = null,
        double captureDeclineRate = 0)
    {
        var options = new PaymentSimulationOptions
        {
            DeclineRate = declineRate,
            TimeoutRate = timeoutRate,
            CaptureDeclineRate = captureDeclineRate,
            LostRequestRate = lostRequestRate,
            MinLatency = TimeSpan.Zero,
            MaxLatency = TimeSpan.Zero,
            Seed = seed
        };

        return (
            new SimulatedPaymentGateway(_database.Scopes, Options.Create(options), TimeProvider.System),
            options);
    }

    private static async Task<IReadOnlyList<GatewayOutcome>> SequenceAsync(SimulatedPaymentGateway gateway)
    {
        var outcomes = new List<GatewayOutcome>();

        for (var attempt = 0; attempt < 40; attempt++)
        {
            var (outcome, _) = await gateway.AuthorizeAsync($"key-{attempt}", Amount, Currency);
            outcomes.Add(outcome);
        }

        return outcomes;
    }
}
