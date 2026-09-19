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
        int? seed = null) =>
        new(
            Options.Create(new PaymentSimulationOptions
            {
                DeclineRate = declineRate,
                TimeoutRate = timeoutRate,
                MinLatency = TimeSpan.Zero,
                MaxLatency = TimeSpan.Zero,
                Seed = seed
            }),
            TimeProvider.System);

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
