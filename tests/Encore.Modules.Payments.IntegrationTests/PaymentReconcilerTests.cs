using Encore.Modules.Payments.Contracts;
using Encore.Modules.Payments.Data;
using Encore.Modules.Payments.Models;
using Encore.Modules.Payments.Simulation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Encore.Modules.Payments.IntegrationTests;

/// <summary>
/// The reconciler: what it settles timed-out attempts to, what it leaves alone, and how it
/// frees the order to be paid for again. Timed-out rows are produced by the real adapter and
/// gateway, with the gateway's options changed between the authorisation and the lookup. One
/// sweep is driven directly per test. A sweep visits every unresolved attempt, so the shared
/// database is emptied before each test.
/// </summary>
/// <remarks>
/// Not covered: a lookup that finds funds held and then fails to release them. Forcing it would
/// need a simulator knob that exists only for this test.
/// </remarks>
public sealed class PaymentReconcilerTests(PaymentsDatabase database)
    : IClassFixture<PaymentsDatabase>, IAsyncLifetime
{
    private const decimal Amount = 120.50m;
    private const string Currency = "GBP";

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Comfortably past <see cref="PaymentReconciliationOptions.MinimumAge"/>.</summary>
    private static readonly DateTime Afterwards = T0.AddMinutes(10);

    private readonly PaymentsDatabase _database = database;

    private readonly DbContextOptions<PaymentsDbContext> _options = database.Options;

    /// <summary>How the gateway reaches its ledger table.</summary>
    private readonly IServiceScopeFactory _scopes = database.Scopes;

    /// <summary>Empties the attempts and the ledger the previous test left.</summary>
    public Task InitializeAsync() => _database.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The authorisation landed but its answer was lost: funds are held, so the reconciler
    /// releases them.
    /// </summary>
    [Fact]
    public async Task Reconcile_WhenTheAuthorisationLanded_ShouldReleaseTheFunds()
    {
        var (orderId, clientId) = NewOrder();
        var gateway = Gateway(lostRequestRate: 0);

        await AuthorizeAsync(gateway, orderId, clientId);

        gateway.Options.TimeoutRate = 0;
        await using var host = Host(gateway);

        Assert.Equal(1, await host.Reconciler.ReconcileBatchAsync(CancellationToken.None));

        var payment = await ReadAsync(orderId);
        Assert.Equal(PaymentStatus.Voided, payment.Status);
        Assert.NotNull(payment.GatewayReference);
        Assert.Equal(Afterwards, payment.ResolvedAt);
        Assert.False(payment.IsLive);
    }

    /// <summary>The request never arrived: nothing held, the attempt is Abandoned.</summary>
    [Fact]
    public async Task Reconcile_WhenTheRequestNeverArrived_ShouldAbandonTheAttempt()
    {
        var (orderId, clientId) = NewOrder();
        var gateway = Gateway(lostRequestRate: 1);

        await AuthorizeAsync(gateway, orderId, clientId);

        gateway.Options.TimeoutRate = 0;
        await using var host = Host(gateway);

        Assert.Equal(1, await host.Reconciler.ReconcileBatchAsync(CancellationToken.None));

        var payment = await ReadAsync(orderId);
        Assert.Equal(PaymentStatus.Abandoned, payment.Status);
        Assert.Null(payment.GatewayReference);
        Assert.False(payment.IsLive);
    }

    /// <summary>A refusal whose answer was lost is settled as Declined.</summary>
    [Fact]
    public async Task Reconcile_WhenTheGatewayHadAlreadyRefused_ShouldRecordTheDecline()
    {
        var (orderId, clientId) = NewOrder();
        var gateway = Gateway(declineRate: 1, lostRequestRate: 0);

        await AuthorizeAsync(gateway, orderId, clientId);

        gateway.Options.TimeoutRate = 0;
        await using var host = Host(gateway);

        Assert.Equal(1, await host.Reconciler.ReconcileBatchAsync(CancellationToken.None));

        var payment = await ReadAsync(orderId);
        Assert.Equal(PaymentStatus.Declined, payment.Status);
        Assert.False(payment.IsLive);
    }

    /// <summary>A lookup with no answer writes nothing.</summary>
    [Fact]
    public async Task Reconcile_WhenTheLookupGetsNoAnswer_ShouldLeaveTheAttemptUnresolved()
    {
        var (orderId, clientId) = NewOrder();
        var gateway = Gateway(lostRequestRate: 0);

        await AuthorizeAsync(gateway, orderId, clientId);

        // TimeoutRate stays at 1, so the lookup hangs up too.
        await using var host = Host(gateway);

        Assert.Equal(0, await host.Reconciler.ReconcileBatchAsync(CancellationToken.None));

        var payment = await ReadAsync(orderId);
        Assert.Equal(PaymentStatus.TimedOut, payment.Status);
        Assert.True(payment.IsLive);
    }

    /// <summary>An attempt younger than MinimumAge is left for the confirm that may still retry it.</summary>
    [Fact]
    public async Task Reconcile_WhenTheAttemptIsTooYoung_ShouldLeaveItAlone()
    {
        var (orderId, clientId) = NewOrder();
        var gateway = Gateway(lostRequestRate: 0);

        await AuthorizeAsync(gateway, orderId, clientId);

        gateway.Options.TimeoutRate = 0;
        await using var host = Host(gateway, at: T0.AddMinutes(1));

        Assert.Equal(0, await host.Reconciler.ReconcileBatchAsync(CancellationToken.None));

        Assert.Equal(PaymentStatus.TimedOut, (await ReadAsync(orderId)).Status);
    }

    /// <summary>An authorisation that was answered is not the reconciler's business.</summary>
    [Fact]
    public async Task Reconcile_ShouldNotTouchAnAttemptThatGotAnAnswer()
    {
        var (orderId, clientId) = NewOrder();
        var gateway = Gateway();
        gateway.Options.TimeoutRate = 0;

        await AuthorizeAsync(gateway, orderId, clientId);
        Assert.Equal(PaymentStatus.Authorized, (await ReadAsync(orderId)).Status);

        await using var host = Host(gateway);

        Assert.Equal(0, await host.Reconciler.ReconcileBatchAsync(CancellationToken.None));
        Assert.Equal(PaymentStatus.Authorized, (await ReadAsync(orderId)).Status);
    }

    /// <summary>Settling a timed-out attempt frees the order's live slot for a new attempt.</summary>
    [Fact]
    public async Task Reconcile_ShouldLetTheOrderBePaidForAgain()
    {
        var (orderId, clientId) = NewOrder();
        var gateway = Gateway(lostRequestRate: 1);

        await AuthorizeAsync(gateway, orderId, clientId);

        gateway.Options.TimeoutRate = 0;
        await using var host = Host(gateway);
        await host.Reconciler.ReconcileBatchAsync(CancellationToken.None);

        var second = await AuthorizeAsync(gateway, orderId, clientId);

        Assert.Equal(AuthorizePaymentStatus.Authorized, second.Status);

        var attempts = await ReadAllAsync(orderId);

        // Two rows, two keys: the settled attempt was not reopened.
        Assert.Equal(2, attempts.Count);
        Assert.Equal(2, attempts.Select(attempt => attempt.IdempotencyKey).Distinct().Count());
        Assert.Single(attempts, attempt => attempt.Status is PaymentStatus.Abandoned);

        var live = Assert.Single(attempts, attempt => attempt.IsLive);
        Assert.Equal(PaymentStatus.Authorized, live.Status);
    }

    /// <summary>A settled attempt is not asked about again.</summary>
    [Fact]
    public async Task Reconcile_ShouldNotRevisitAnAttemptItHasSettled()
    {
        var (orderId, clientId) = NewOrder();
        var gateway = Gateway(lostRequestRate: 0);

        await AuthorizeAsync(gateway, orderId, clientId);

        gateway.Options.TimeoutRate = 0;
        await using var host = Host(gateway);

        Assert.Equal(1, await host.Reconciler.ReconcileBatchAsync(CancellationToken.None));
        Assert.Equal(0, await host.Reconciler.ReconcileBatchAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Reconcile_WhenThereIsNothingToDo_ShouldSettleNothing()
    {
        await using var host = Host(Gateway());

        Assert.Equal(0, await host.Reconciler.ReconcileBatchAsync(CancellationToken.None));
    }

    /// <summary>The batch caps gateway calls per sweep; the oldest attempts go first.</summary>
    [Fact]
    public async Task Reconcile_ShouldSettleNoMoreThanOneBatch()
    {
        var gateway = Gateway(lostRequestRate: 1);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var (orderId, clientId) = NewOrder();
            await AuthorizeAsync(gateway, orderId, clientId);
        }

        gateway.Options.TimeoutRate = 0;
        await using var host = Host(gateway, configure: options => options.BatchSize = 2);

        Assert.Equal(2, await host.Reconciler.ReconcileBatchAsync(CancellationToken.None));
        Assert.Equal(1, await host.Reconciler.ReconcileBatchAsync(CancellationToken.None));
        Assert.Equal(0, await host.Reconciler.ReconcileBatchAsync(CancellationToken.None));
    }

    /// <summary>
    /// While another connection holds the advisory lock, a sweep does nothing; once it is
    /// released, the next sweep does the work.
    /// </summary>
    [Fact]
    public async Task Reconcile_WhenAnotherInstanceHoldsTheLease_ShouldSettleNothing()
    {
        var (orderId, clientId) = NewOrder();
        var gateway = Gateway(lostRequestRate: 1);

        await AuthorizeAsync(gateway, orderId, clientId);

        gateway.Options.TimeoutRate = 0;
        await using var host = Host(gateway);

        await using (var holderContext = new PaymentsDbContext(_options))
        await using (var holderTransaction = await holderContext.Database.BeginTransactionAsync())
        {
            await holderContext.Database
                .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({PaymentReconciler.LeaseKey}) AS \"Value\"")
                .SingleAsync();

            Assert.Equal(0, await host.Reconciler.ReconcileBatchAsync(CancellationToken.None));

            var stillTimedOut = await ReadAsync(orderId);
            Assert.Equal(PaymentStatus.TimedOut, stillTimedOut.Status);
            Assert.True(stillTimedOut.IsLive);
        }

        // The holder's transaction ended, releasing the lease.
        Assert.Equal(1, await host.Reconciler.ReconcileBatchAsync(CancellationToken.None));
        Assert.Equal(PaymentStatus.Abandoned, (await ReadAsync(orderId)).Status);
    }

    /// <summary>
    /// A confirm that retries the attempt while the sweep is between its lookup and its void
    /// waits for the row, then loses. It never revives an authorisation the sweep is
    /// releasing, and the next confirm pays under a fresh key.
    /// </summary>
    [Fact]
    public async Task Reconcile_WhenAConfirmRetriesMidSweep_ShouldHoldItOffUntilTheFundsAreReleased()
    {
        var (orderId, clientId) = NewOrder();
        var gateway = Gateway(lostRequestRate: 0);

        await AuthorizeAsync(gateway, orderId, clientId);

        // Slow enough that the retry below lands inside the sweep's lookup.
        gateway.Options.TimeoutRate = 0;
        gateway.Options.MinLatency = gateway.Options.MaxLatency = TimeSpan.FromSeconds(1);
        await using var host = Host(gateway);

        var sweep = host.Reconciler.ReconcileBatchAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        var retried = await AuthorizeAsync(gateway, orderId, clientId);

        Assert.Equal(1, await sweep);
        Assert.Equal(AuthorizePaymentStatus.ConcurrentAttemptInFlight, retried.Status);
        Assert.Equal(PaymentStatus.Voided, (await ReadAsync(orderId)).Status);

        gateway.Options.MinLatency = gateway.Options.MaxLatency = TimeSpan.Zero;
        var fresh = await AuthorizeAsync(gateway, orderId, clientId);

        Assert.Equal(AuthorizePaymentStatus.Authorized, fresh.Status);
        Assert.Equal(2, (await ReadAllAsync(orderId)).Select(attempt => attempt.IdempotencyKey).Distinct().Count());
    }

    /// <summary>
    /// A crash between the gateway call and its save leaves the attempt pending. If the
    /// gateway did decide it, the funds are found and released like any timed-out attempt.
    /// </summary>
    [Fact]
    public async Task Reconcile_WhenACrashLeftAnAttemptPending_ShouldReleaseWhatTheGatewayHeld()
    {
        var (orderId, clientId) = NewOrder();
        var gateway = Gateway(lostRequestRate: 0);

        await CrashedAttemptAsync(gateway, orderId, clientId, landed: true);

        gateway.Options.TimeoutRate = 0;
        await using var host = Host(gateway);

        Assert.Equal(1, await host.Reconciler.ReconcileBatchAsync(CancellationToken.None));

        var payment = await ReadAsync(orderId);
        Assert.Equal(PaymentStatus.Voided, payment.Status);
        Assert.NotNull(payment.GatewayReference);
        Assert.False(payment.IsLive);
    }

    /// <summary>A crashed attempt the gateway never saw frees the order's live slot.</summary>
    [Fact]
    public async Task Reconcile_WhenACrashLeftAnAttemptPendingThatNeverArrived_ShouldAbandonIt()
    {
        var (orderId, clientId) = NewOrder();
        var gateway = Gateway();

        await CrashedAttemptAsync(gateway, orderId, clientId, landed: false);

        gateway.Options.TimeoutRate = 0;
        await using var host = Host(gateway);

        Assert.Equal(1, await host.Reconciler.ReconcileBatchAsync(CancellationToken.None));
        Assert.Equal(PaymentStatus.Abandoned, (await ReadAsync(orderId)).Status);
    }

    /// <summary>
    /// A crashed attempt whose lookup gets no answer is still claimed as timed out, so it is
    /// counted and asked about again.
    /// </summary>
    [Fact]
    public async Task Reconcile_WhenACrashedAttemptsLookupGetsNoAnswer_ShouldRecordItAsTimedOut()
    {
        var (orderId, clientId) = NewOrder();
        var gateway = Gateway();

        await CrashedAttemptAsync(gateway, orderId, clientId, landed: false);

        await using var host = Host(gateway);

        Assert.Equal(0, await host.Reconciler.ReconcileBatchAsync(CancellationToken.None));

        var payment = await ReadAsync(orderId);
        Assert.Equal(PaymentStatus.TimedOut, payment.Status);
        Assert.Equal(Afterwards, payment.ResolvedAt);
    }

    /// <summary>A pending attempt younger than MinimumAge may still be in flight, so it is left alone.</summary>
    [Fact]
    public async Task Reconcile_WhenAPendingAttemptIsRecent_ShouldLeaveItToItsConfirm()
    {
        var (orderId, clientId) = NewOrder();
        var gateway = Gateway();

        await CrashedAttemptAsync(gateway, orderId, clientId, landed: false);

        gateway.Options.TimeoutRate = 0;
        await using var host = Host(gateway, at: T0.AddMinutes(1));

        Assert.Equal(0, await host.Reconciler.ReconcileBatchAsync(CancellationToken.None));
        Assert.Equal(PaymentStatus.Pending, (await ReadAsync(orderId)).Status);
    }

    // -- Scaffolding ------------------------------------------------------

    private static (Guid OrderId, Guid ClientId) NewOrder() => (Guid.NewGuid(), Guid.NewGuid());

    /// <summary>
    /// An attempt as a crash leaves it: recorded pending at <see cref="T0"/> and never answered.
    /// When <paramref name="landed"/>, the gateway did receive it and decided.
    /// </summary>
    private async Task CrashedAttemptAsync(TestGateway gateway, Guid orderId, Guid clientId, bool landed)
    {
        var paymentId = Guid.NewGuid();
        var key = $"order-{orderId:N}-{paymentId:N}";

        await using (var context = new PaymentsDbContext(_options))
        {
            context.Payments.Add(Payment.Create(paymentId, orderId, clientId, Amount, Currency, key, T0));
            await context.SaveChangesAsync();
        }

        if (landed)
        {
            await gateway.Gateway.AuthorizeAsync(key, Amount, Currency);
        }
    }

    /// <summary>A gateway that hangs up on every call, with its options so a test can change that.</summary>
    private TestGateway Gateway(double declineRate = 0, double lostRequestRate = 0.5)
    {
        var options = new PaymentSimulationOptions
        {
            DeclineRate = declineRate,
            TimeoutRate = 1,
            LostRequestRate = lostRequestRate,
            MinLatency = TimeSpan.Zero,
            MaxLatency = TimeSpan.Zero
        };

        return new TestGateway(
            new SimulatedPaymentGateway(_scopes, Options.Create(options), TimeProvider.System),
            options);
    }

    /// <summary>Drives the real adapter, so the row is produced the way rows are.</summary>
    private async Task<AuthorizePaymentResponse> AuthorizeAsync(
        TestGateway gateway,
        Guid orderId,
        Guid clientId)
    {
        await using var context = new PaymentsDbContext(_options);

        var payments = new InProcessOrderPayments(context, gateway.Gateway, new FakeTimeProvider(T0));

        return await payments.AuthorizeAsync(
            new AuthorizePaymentRequest(orderId, clientId, Amount, Currency));
    }

    private ReconcilerHost Host(
        TestGateway gateway,
        DateTime? at = null,
        Action<PaymentReconciliationOptions>? configure = null)
    {
        var options = new PaymentReconciliationOptions();
        configure?.Invoke(options);

        var services = new ServiceCollection();

        services.AddDbContext<PaymentsDbContext>(builder => builder.UsePaymentsNpgsql(_database.ConnectionString));

        var provider = services.BuildServiceProvider();

        var reconciler = new PaymentReconciler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            gateway.Gateway,
            Options.Create(options),
            new FakeTimeProvider(at ?? Afterwards),
            NullLogger<PaymentReconciler>.Instance);

        return new ReconcilerHost(provider, reconciler);
    }

    /// <summary>The one attempt against this order.</summary>
    private async Task<Payment> ReadAsync(Guid orderId)
    {
        await using var context = new PaymentsDbContext(_options);

        return await context.Payments
            .AsNoTracking()
            .SingleAsync(payment => payment.OrderId == orderId);
    }

    /// <summary>
    /// Every attempt against this order, unordered: both rows share an AttemptedAt, so tests
    /// assert on the set.
    /// </summary>
    private async Task<IReadOnlyList<Payment>> ReadAllAsync(Guid orderId)
    {
        await using var context = new PaymentsDbContext(_options);

        return await context.Payments
            .AsNoTracking()
            .Where(payment => payment.OrderId == orderId)
            .ToListAsync();
    }

    private sealed record TestGateway(SimulatedPaymentGateway Gateway, PaymentSimulationOptions Options);

    private sealed class ReconcilerHost(ServiceProvider provider, PaymentReconciler reconciler)
        : IAsyncDisposable
    {
        internal PaymentReconciler Reconciler { get; } = reconciler;

        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }
}
