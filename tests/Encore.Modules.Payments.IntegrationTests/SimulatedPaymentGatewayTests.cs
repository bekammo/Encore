using Encore.Modules.Payments.Data;
using Encore.Modules.Payments.Simulation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Payments.IntegrationTests;

/// <summary>
/// One Postgres shared by every gateway test, with the ledger emptied between tests. A class
/// fixture, since a container per test would start forty.
/// </summary>
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
/// The simulated gateway: it honours idempotency keys, a seeded run is reproducible, and its
/// decisions outlive the process. Integration tests because its memory is a table; zero
/// latency throughout.
/// </summary>
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
    /// The gateway and the options it reads, so a test can change conditions between calls (for
    /// example, time out the authorisation, then answer the lookup).
    /// </summary>
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

    /// <summary>A timeout outranks a decline.</summary>
    [Fact]
    public async Task Authorize_WhenAlwaysTimingOut_ShouldTimeOutEvenIfAlsoAlwaysDeclining()
    {
        var (outcome, reference) = await Gateway(declineRate: 1, timeoutRate: 1)
            .AuthorizeAsync("key-1", Amount, Currency);

        Assert.Equal(GatewayOutcome.TimedOut, outcome);
        Assert.Null(reference);
    }

    /// <summary>Capture and void are never declined: a known simplification.</summary>
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
    /// Fifty authorisations under one key at a 50% decline rate all agree. Unseeded on purpose.
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

    // -- Surviving the process --------------------------------------------

    /// <summary>
    /// A second gateway instance over the same database knows what the first decided, so a
    /// restart cannot turn held funds into NotFound.
    /// </summary>
    [Fact]
    public async Task LookUp_FromAnInstanceThatNeverAuthorised_ShouldStillReportTheHold()
    {
        var authorising = Gateway();
        var (outcome, reference) = await authorising.AuthorizeAsync("key-1", Amount, Currency);
        Assert.Equal(GatewayOutcome.Succeeded, outcome);

        // A restart, or a second process: same database, no shared memory.
        var restarted = Gateway();

        var (record, found) = await restarted.LookUpAsync("key-1");

        Assert.Equal(GatewayRecord.Authorized, record);
        Assert.Equal(reference, found);
    }

    /// <summary>A restarted gateway does not re-roll a decline.</summary>
    [Fact]
    public async Task Authorize_FromAnotherInstance_ShouldRepeatTheRecordedAnswer()
    {
        var (outcome, _) = await Gateway(declineRate: 1).AuthorizeAsync("key-1", Amount, Currency);
        Assert.Equal(GatewayOutcome.Declined, outcome);

        // Opposite conditions: without the ledger this would roll again and succeed.
        var (again, _) = await Gateway(declineRate: 0).AuthorizeAsync("key-1", Amount, Currency);

        Assert.Equal(GatewayOutcome.Declined, again);
    }

    /// <summary>A request that never arrived leaves no record, so NotFound stays meaningful.</summary>
    [Fact]
    public async Task Authorize_WhenTheRequestWasLost_ShouldLeaveNothingForAnotherInstanceToFind()
    {
        await Gateway(timeoutRate: 1, lostRequestRate: 1).AuthorizeAsync("key-1", Amount, Currency);

        var (record, _) = await Gateway().LookUpAsync("key-1");

        Assert.Equal(GatewayRecord.NotFound, record);
    }

    /// <summary>Concurrent authorisations under one key: the loser reads back the winner's answer.</summary>
    [Fact]
    public async Task Authorize_ConcurrentlyUnderOneKey_ShouldAgreeOnOneAnswer()
    {
        var gateway = Gateway(declineRate: 0.5);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => gateway.AuthorizeAsync("one-key", Amount, Currency)));

        Assert.Single(attempts.Select(attempt => attempt.Outcome).Distinct());
    }

    // -- Reproducibility --------------------------------------------------

    /// <summary>A seeded gateway replays; the ledger is emptied between runs so this proves the seed.</summary>
    [Fact]
    public async Task Authorize_WithTheSameSeed_ShouldProduceTheSameSequence()
    {
        var first = await SequenceAsync(Gateway(declineRate: 0.3, timeoutRate: 0.3, seed: 1234));

        await _database.ResetAsync();

        var again = await SequenceAsync(Gateway(declineRate: 0.3, timeoutRate: 0.3, seed: 1234));

        Assert.Equal(first, again);
    }

    /// <summary>An unseeded gateway does not replay.</summary>
    [Fact]
    public async Task Authorize_WithoutASeed_ShouldNotProduceTheSameSequence()
    {
        var first = await SequenceAsync(Gateway(declineRate: 0.3, timeoutRate: 0.3));

        await _database.ResetAsync();

        var again = await SequenceAsync(Gateway(declineRate: 0.3, timeoutRate: 0.3));

        Assert.NotEqual(first, again);
    }

    // -- Latency ----------------------------------------------------------

    /// <summary>A maximum latency below the minimum clamps up instead of throwing.</summary>
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
    /// A timeout is either a lost request (NotFound on lookup) or a lost answer (the decision on
    /// lookup). Both must be producible.
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

    /// <summary>A refusal whose answer was lost is found as Declined.</summary>
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

    /// <summary>A lookup that gets no answer is Unknown, never NotFound.</summary>
    [Fact]
    public async Task LookUp_WhenTheGatewayDoesNotAnswer_ShouldReportUnknownRatherThanNotFound()
    {
        var (record, reference) = await Gateway(timeoutRate: 1).LookUpAsync("key-1");

        Assert.Equal(GatewayRecord.Unknown, record);
        Assert.Null(reference);
    }

    /// <summary>A lookup records nothing.</summary>
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
