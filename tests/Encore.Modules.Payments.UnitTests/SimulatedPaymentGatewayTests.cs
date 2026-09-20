using Encore.Modules.Payments.Simulation;
using Microsoft.Extensions.Options;

namespace Encore.Modules.Payments.UnitTests;

/// <summary>
/// The simulated gateway's two promises: it honours an idempotency key, and a
/// seeded run is reproducible. Both exist so the failure paths above it can be
/// tested at all — a gateway that forgot its keys would let the retry path pass
/// tests it should fail, and one that could not be pinned would make every
/// assertion about an outcome a coin flip.
/// </summary>
/// <remarks>
/// Every gateway here is built with zero latency, so nothing in this file sleeps.
/// </remarks>
public class SimulatedPaymentGatewayTests
{
    private const decimal Amount = 99.99m;
    private const string Currency = "GBP";

    private static SimulatedPaymentGateway Gateway(
        double declineRate = 0,
        double timeoutRate = 0,
        double lostRequestRate = 0.5,
        int? seed = null) =>
        Configured(declineRate, timeoutRate, lostRequestRate, seed).Gateway;

    /// <summary>
    /// The gateway and the options object it is still reading, so a test can change
    /// the weather between two calls.
    /// </summary>
    /// <remarks>
    /// Every lookup test needs this. Reconciliation only ever asks about an
    /// authorisation that got no answer, so the two calls have to happen under
    /// opposite conditions — <c>TimeoutRate</c> at 1 for the authorisation and 0 for
    /// the lookup — and they have to hit the <i>same</i> gateway, because the record
    /// being looked up lives in that instance. Rebuilding it between the calls would
    /// look like the same test and assert nothing.
    /// </remarks>
    private static (SimulatedPaymentGateway Gateway, PaymentSimulationOptions Options) Configured(
        double declineRate = 0,
        double timeoutRate = 0,
        double lostRequestRate = 0.5,
        int? seed = null)
    {
        var options = new PaymentSimulationOptions
        {
            DeclineRate = declineRate,
            TimeoutRate = timeoutRate,
            LostRequestRate = lostRequestRate,
            MinLatency = TimeSpan.Zero,
            MaxLatency = TimeSpan.Zero,
            Seed = seed
        };

        return (new SimulatedPaymentGateway(Options.Create(options), TimeProvider.System), options);
    }

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

    /// <summary>
    /// A timeout outranks a decline: the gateway cannot both refuse and fail to
    /// answer, and "no answer" is the one that leaves the caller unsure.
    /// </summary>
    [Fact]
    public async Task Authorize_WhenAlwaysTimingOut_ShouldTimeOutEvenIfAlsoAlwaysDeclining()
    {
        var (outcome, reference) = await Gateway(declineRate: 1, timeoutRate: 1)
            .AuthorizeAsync("key-1", Amount, Currency);

        Assert.Equal(GatewayOutcome.TimedOut, outcome);
        Assert.Null(reference);
    }

    /// <summary>
    /// Capture and void are never declined here. That is a deliberate
    /// simplification rather than a claim about payments — see
    /// <c>DECISIONS.md</c> 032.
    /// </summary>
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

    // -- Idempotency ------------------------------------------------------

    /// <summary>
    /// The load-bearing test in this file. Fifty authorisations under one key
    /// against a gateway that refuses half the time: without the memory these
    /// would disagree, so agreement is the memory working. Unseeded on purpose —
    /// a seed would make the sequence fixed and prove nothing about the key.
    /// </summary>
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

    // -- Reproducibility --------------------------------------------------

    /// <summary>
    /// A seeded gateway replays. This is what lets a load test be re-run against
    /// the same sequence of nastiness rather than a fresh one.
    /// </summary>
    [Fact]
    public async Task Authorize_WithTheSameSeed_ShouldProduceTheSameSequence()
    {
        var first = await SequenceAsync(Gateway(declineRate: 0.3, timeoutRate: 0.3, seed: 1234));
        var again = await SequenceAsync(Gateway(declineRate: 0.3, timeoutRate: 0.3, seed: 1234));

        Assert.Equal(first, again);
    }

    /// <summary>
    /// And an unseeded one does not. Rates of a third each over forty calls make
    /// two identical runs vanishingly unlikely, which is what makes this an
    /// assertion rather than a hope.
    /// </summary>
    [Fact]
    public async Task Authorize_WithoutASeed_ShouldNotProduceTheSameSequence()
    {
        var first = await SequenceAsync(Gateway(declineRate: 0.3, timeoutRate: 0.3));
        var again = await SequenceAsync(Gateway(declineRate: 0.3, timeoutRate: 0.3));

        Assert.NotEqual(first, again);
    }

    // -- Latency ----------------------------------------------------------

    /// <summary>
    /// A maximum below the minimum is a typo in configuration, not a reason to
    /// refuse to start. It clamps up, so the gateway gets slower rather than
    /// throwing at a point where nothing can act on the error.
    /// </summary>
    [Fact]
    public async Task Authorize_WhenMaxLatencyIsBelowMin_ShouldNotThrow()
    {
        var gateway = new SimulatedPaymentGateway(
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

    /// <summary>
    /// The two branches reconciliation turns on, and the reason
    /// <c>LostRequestRate</c> exists: a timeout means either that the request never
    /// arrived or that the answer never came back, and only the gateway can say
    /// which. These are the tests that would catch a simulator that had quietly
    /// stopped being able to produce one of the two.
    /// </summary>
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

    /// <summary>
    /// A refusal whose answer was lost is still a refusal, and this is the one route
    /// by which a timed-out attempt may honestly end up declined — the gateway said
    /// so, rather than a silence being read as if it had.
    /// </summary>
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

    /// <summary>
    /// The distinction the reconciler's correctness rests on. "I looked and there is
    /// nothing" releases the order's live-attempt slot; "I could not look" must not,
    /// because the funds may be held and nobody has established otherwise.
    /// </summary>
    [Fact]
    public async Task LookUp_WhenTheGatewayDoesNotAnswer_ShouldReportUnknownRatherThanNotFound()
    {
        var (record, reference) = await Gateway(timeoutRate: 1).LookUpAsync("key-1");

        Assert.Equal(GatewayRecord.Unknown, record);
        Assert.Null(reference);
    }

    /// <summary>
    /// A lookup is a read. If it recorded an answer of its own, the first thing
    /// reconciliation did to an attempt would be to decide it — and the decision
    /// would be this gateway's coin flip rather than anything that happened.
    /// </summary>
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
