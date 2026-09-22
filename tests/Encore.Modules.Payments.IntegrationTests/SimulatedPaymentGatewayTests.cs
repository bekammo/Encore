using Encore.Modules.Payments.Data;
using Encore.Modules.Payments.Simulation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Payments.IntegrationTests;

/// <summary>
/// One Postgres for every test in <see cref="SimulatedPaymentGatewayTests"/>, plus
/// the scope factory the gateway now needs and a way to empty its ledger between
/// tests.
/// </summary>
/// <remarks>
/// A class fixture rather than a container per test: xUnit builds a new test class
/// instance per test, so the <c>IAsyncLifetime</c> pattern the other suites here use
/// would start forty containers for this file. The fixture starts one and
/// <see cref="ResetAsync"/> gives each test a clean ledger, which is what those tests
/// actually need.
/// </remarks>
public sealed class GatewayLedgerDatabase : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("encore")
        .WithUsername("encore")
        .WithPassword("encore")
        .Build();

    private ServiceProvider _provider = null!;

    /// <summary>What the gateway resolves its context through.</summary>
    public IServiceScopeFactory Scopes { get; private set; } = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var connectionString = _postgres.GetConnectionString();

        await using (var context = new PaymentsDbContext(
            new DbContextOptionsBuilder<PaymentsDbContext>().UsePaymentsNpgsql(connectionString).Options))
        {
            await context.Database.MigrateAsync();
        }

        var services = new ServiceCollection();
        services.AddDbContext<PaymentsDbContext>(options => options.UsePaymentsNpgsql(connectionString));

        _provider = services.BuildServiceProvider();
        Scopes = _provider.GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>Empties the gateway's ledger, so the next test starts with a gateway that has answered nothing.</summary>
    public async Task ResetAsync()
    {
        using var scope = Scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        await context.Database.ExecuteSqlRawAsync(
            $"TRUNCATE TABLE \"{PaymentsPersistence.Schema}\".\"gateway_ledger\"");
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

/// <summary>
/// The simulated gateway's three promises: it honours an idempotency key, a seeded
/// run is reproducible, and — since <c>DECISIONS.md</c> 066 — what it decided
/// outlives the process that decided it. The first two exist so the failure paths
/// above it can be tested at all; the third exists because 064 proved the system
/// depended on it while nothing provided it.
/// </summary>
/// <remarks>
/// <para>
/// <b>These were unit tests until 066 and are integration tests now</b>, because the
/// gateway's memory is a table. That is a real cost — forty fast tests became forty
/// tests behind a container — and it was paid rather than avoided: the alternative
/// was an <c>IGatewayLedger</c> with a real implementation and an in-memory one,
/// which is the repository interface 001 forbids in a flat module, introduced so
/// that tests could keep using the very mechanism the entry exists to remove.
/// </para>
/// <para>
/// Every gateway here is built with zero latency, so nothing in this file sleeps.
/// </para>
/// </remarks>
public sealed class SimulatedPaymentGatewayTests(GatewayLedgerDatabase database)
    : IClassFixture<GatewayLedgerDatabase>, IAsyncLifetime
{
    private const decimal Amount = 99.99m;
    private const string Currency = "GBP";

    private readonly GatewayLedgerDatabase _database = database;

    /// <inheritdoc />
    public Task InitializeAsync() => _database.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    private SimulatedPaymentGateway Gateway(
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
    /// the lookup. They no longer have to hit the same <i>instance</i>, which is the
    /// whole of 066; they do anyway, because the weather is what is being changed.
    /// </remarks>
    private (SimulatedPaymentGateway Gateway, PaymentSimulationOptions Options) Configured(
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

        return (
            new SimulatedPaymentGateway(_database.Scopes, Options.Create(options), TimeProvider.System),
            options);
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

    // -- Surviving the process (DECISIONS 066) ----------------------------

    /// <summary>
    /// The regression test for the failure 064 found, stated as plainly as it can be:
    /// a gateway that never authorised anything still knows what the one that did
    /// decided.
    /// </summary>
    /// <remarks>
    /// Two instances over one database is both halves of 064's problem at once — the
    /// restart in fault 1, where <c>payments-api</c> came back with an empty
    /// dictionary and settled 120 of 121 attempts as abandoned, and fault 2's second
    /// process asking about keys it had never seen. Before 066 this returned
    /// <c>NotFound</c>, which the reconciler reads as "the gateway looked and there is
    /// nothing there" and acts on by releasing an order's live-attempt slot while the
    /// funds are still held.
    /// </remarks>
    [Fact]
    public async Task LookUp_FromAnInstanceThatNeverAuthorised_ShouldStillReportTheHold()
    {
        var authorising = Gateway();
        var (outcome, reference) = await authorising.AuthorizeAsync("key-1", Amount, Currency);
        Assert.Equal(GatewayOutcome.Succeeded, outcome);

        // A restart, or a second process. Same database, no shared state in memory.
        var restarted = Gateway();

        var (record, found) = await restarted.LookUpAsync("key-1");

        Assert.Equal(GatewayRecord.Authorized, record);
        Assert.Equal(reference, found);
    }

    /// <summary>
    /// And the same for a decline: a restarted gateway must not re-roll a decision
    /// somebody has already been given.
    /// </summary>
    [Fact]
    public async Task Authorize_FromAnotherInstance_ShouldRepeatTheRecordedAnswer()
    {
        var (outcome, _) = await Gateway(declineRate: 1).AuthorizeAsync("key-1", Amount, Currency);
        Assert.Equal(GatewayOutcome.Declined, outcome);

        // Different instance, opposite weather: without the ledger this would roll
        // again and succeed, which is a customer told "declined" and then charged.
        var (again, _) = await Gateway(declineRate: 0).AuthorizeAsync("key-1", Amount, Currency);

        Assert.Equal(GatewayOutcome.Declined, again);
    }

    /// <summary>
    /// A request that never arrived leaves nothing behind, and that has to stay true
    /// now the record is durable — it is what makes <c>NotFound</c> mean something to
    /// the reconciler (057).
    /// </summary>
    [Fact]
    public async Task Authorize_WhenTheRequestWasLost_ShouldLeaveNothingForAnotherInstanceToFind()
    {
        await Gateway(timeoutRate: 1, lostRequestRate: 1).AuthorizeAsync("key-1", Amount, Currency);

        var (record, _) = await Gateway().LookUpAsync("key-1");

        Assert.Equal(GatewayRecord.NotFound, record);
    }

    /// <summary>
    /// Concurrent authorisations under one key are arbitrated by the ledger's primary
    /// key, and the loser reads back the winner's answer rather than its own roll.
    /// </summary>
    [Fact]
    public async Task Authorize_ConcurrentlyUnderOneKey_ShouldAgreeOnOneAnswer()
    {
        var gateway = Gateway(declineRate: 0.5);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => gateway.AuthorizeAsync("one-key", Amount, Currency)));

        Assert.Single(attempts.Select(attempt => attempt.Outcome).Distinct());
    }

    // -- Reproducibility --------------------------------------------------

    /// <summary>
    /// A seeded gateway replays. This is what lets a load test be re-run against
    /// the same sequence of nastiness rather than a fresh one.
    /// </summary>
    /// <remarks>
    /// The ledger is emptied between the two runs, and since 066 that is not
    /// housekeeping but the point: a second run over the first's rows would return
    /// the recorded answers and agree with itself no matter what the seed did.
    /// </remarks>
    [Fact]
    public async Task Authorize_WithTheSameSeed_ShouldProduceTheSameSequence()
    {
        var first = await SequenceAsync(Gateway(declineRate: 0.3, timeoutRate: 0.3, seed: 1234));

        await _database.ResetAsync();

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

        await _database.ResetAsync();

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
