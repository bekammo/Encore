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
/// Rows come from the real adapter and gateway, with the gateway's options changed between the
/// authorisation and the lookup. A sweep visits every unresolved attempt, so the database is
/// emptied before each test.
/// </summary>
/// <remarks>
/// Not covered: a lookup that finds funds held, then a failed release. Forcing it would need a
/// simulator knob that exists only for this test.
/// </remarks>
public sealed class PaymentReconcilerTests(PaymentsDatabase database)
    : IClassFixture<PaymentsDatabase>, IAsyncLifetime
{
    private const decimal Amount = 120.50m;
    private const string Currency = "GBP";

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // Comfortably past PaymentReconciliationOptions.MinimumAge.
    private static readonly DateTime Afterwards = T0.AddMinutes(10);

    private readonly PaymentsDatabase _database = database;

    private readonly DbContextOptions<PaymentsDbContext> _options = database.Options;

    private readonly IServiceScopeFactory _scopes = database.Scopes;

    public Task InitializeAsync() => _database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Reconcile_WhenTheAuthorisationLanded_ShouldReleaseTheFunds()
    {
        var (orderId, clientId) = NewOrder();
        var gateway = Gateway(lostRequestRate: 0);

        await AuthorizeAsync(gateway, orderId, clientId);

        gateway.Options.TimeoutRate = 0;
        var reconciler = Reconciler(gateway);

        Assert.Equal(1, await reconciler.ReconcileBatchAsync(CancellationToken.None));

        var payment = await ReadAsync(orderId);
        Assert.Equal(PaymentStatus.Voided, payment.Status);
        Assert.NotNull(payment.GatewayReference);
        Assert.Equal(Afterwards, payment.ResolvedAt);
        Assert.False(payment.IsLive);
    }

    [Fact]
    public async Task Reconcile_WhenTheRequestNeverArrived_ShouldAbandonTheAttempt()
    {
        var (orderId, clientId) = NewOrder();
        var gateway = Gateway(lostRequestRate: 1);

        await AuthorizeAsync(gateway, orderId, clientId);

        gateway.Options.TimeoutRate = 0;
        var reconciler = Reconciler(gateway);

        Assert.Equal(1, await reconciler.ReconcileBatchAsync(CancellationToken.None));

        var payment = await ReadAsync(orderId);
        Assert.Equal(PaymentStatus.Abandoned, payment.Status);
        Assert.Null(payment.GatewayReference);
        Assert.False(payment.IsLive);
    }

    [Fact]
    public async Task Reconcile_WhenTheGatewayHadAlreadyRefused_ShouldRecordTheDecline()
    {
        var (orderId, clientId) = NewOrder();
        var gateway = Gateway(declineRate: 1, lostRequestRate: 0);

        await AuthorizeAsync(gateway, orderId, clientId);

        gateway.Options.TimeoutRate = 0;
        var reconciler = Reconciler(gateway);

        Assert.Equal(1, await reconciler.ReconcileBatchAsync(CancellationToken.None));

        var payment = await ReadAsync(orderId);
        Assert.Equal(PaymentStatus.Declined, payment.Status);
        Assert.False(payment.IsLive);
    }

    /// <summary>Unknown is not NotFound: nothing is written and the attempt stays live (014).</summary>
    [Fact]
    public async Task Reconcile_WhenTheLookupGetsNoAnswer_ShouldLeaveTheAttemptUnresolved()
    {
        var (orderId, clientId) = NewOrder();
        var gateway = Gateway(lostRequestRate: 0);

        await AuthorizeAsync(gateway, orderId, clientId);

        // TimeoutRate stays at 1, so the lookup hangs up too.
        var reconciler = Reconciler(gateway);

        Assert.Equal(0, await reconciler.ReconcileBatchAsync(CancellationToken.None));

        var payment = await ReadAsync(orderId);
        Assert.Equal(PaymentStatus.TimedOut, payment.Status);
        Assert.True(payment.IsLive);
    }

    [Fact]
    public async Task Reconcile_WhenTheAttemptIsTooYoung_ShouldLeaveItAlone()
    {
        var (orderId, clientId) = NewOrder();
        var gateway = Gateway(lostRequestRate: 0);

        await AuthorizeAsync(gateway, orderId, clientId);

        gateway.Options.TimeoutRate = 0;
        var reconciler = Reconciler(gateway, at: T0.AddMinutes(1));

        Assert.Equal(0, await reconciler.ReconcileBatchAsync(CancellationToken.None));

        Assert.Equal(PaymentStatus.TimedOut, (await ReadAsync(orderId)).Status);
    }

    [Fact]
    public async Task Reconcile_ShouldNotTouchAnAttemptThatGotAnAnswer()
    {
        var (orderId, clientId) = NewOrder();
        var gateway = Gateway();
        gateway.Options.TimeoutRate = 0;

        await AuthorizeAsync(gateway, orderId, clientId);
        Assert.Equal(PaymentStatus.Authorized, (await ReadAsync(orderId)).Status);

        var reconciler = Reconciler(gateway);

        Assert.Equal(0, await reconciler.ReconcileBatchAsync(CancellationToken.None));
        Assert.Equal(PaymentStatus.Authorized, (await ReadAsync(orderId)).Status);
    }

    [Fact]
    public async Task Reconcile_ShouldLetTheOrderBePaidForAgain()
    {
        var (orderId, clientId) = NewOrder();
        var gateway = Gateway(lostRequestRate: 1);

        await AuthorizeAsync(gateway, orderId, clientId);

        gateway.Options.TimeoutRate = 0;
        var reconciler = Reconciler(gateway);
        await reconciler.ReconcileBatchAsync(CancellationToken.None);

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

    [Fact]
    public async Task Reconcile_ShouldNotRevisitAnAttemptItHasSettled()
    {
        var (orderId, clientId) = NewOrder();
        var gateway = Gateway(lostRequestRate: 0);

        await AuthorizeAsync(gateway, orderId, clientId);

        gateway.Options.TimeoutRate = 0;
        var reconciler = Reconciler(gateway);

        Assert.Equal(1, await reconciler.ReconcileBatchAsync(CancellationToken.None));
        Assert.Equal(0, await reconciler.ReconcileBatchAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Reconcile_WhenThereIsNothingToDo_ShouldSettleNothing()
    {
        var reconciler = Reconciler(Gateway());

        Assert.Equal(0, await reconciler.ReconcileBatchAsync(CancellationToken.None));
    }

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
        var reconciler = Reconciler(gateway, configure: options => options.BatchSize = 2);

        Assert.Equal(2, await reconciler.ReconcileBatchAsync(CancellationToken.None));
        Assert.Equal(1, await reconciler.ReconcileBatchAsync(CancellationToken.None));
        Assert.Equal(0, await reconciler.ReconcileBatchAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Reconcile_WhenAnotherInstanceHoldsTheLease_ShouldSettleNothing()
    {
        var (orderId, clientId) = NewOrder();
        var gateway = Gateway(lostRequestRate: 1);

        await AuthorizeAsync(gateway, orderId, clientId);

        gateway.Options.TimeoutRate = 0;
        var reconciler = Reconciler(gateway);

        await using (var holderContext = new PaymentsDbContext(_options))
        await using (var holderTransaction = await holderContext.Database.BeginTransactionAsync())
        {
            await holderContext.Database
                .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({PaymentReconciler.LeaseKey}) AS \"Value\"")
                .SingleAsync();

            Assert.Equal(0, await reconciler.ReconcileBatchAsync(CancellationToken.None));

            var stillTimedOut = await ReadAsync(orderId);
            Assert.Equal(PaymentStatus.TimedOut, stillTimedOut.Status);
            Assert.True(stillTimedOut.IsLive);
        }

        // The holder's transaction ended, releasing the lease.
        Assert.Equal(1, await reconciler.ReconcileBatchAsync(CancellationToken.None));
        Assert.Equal(PaymentStatus.Abandoned, (await ReadAsync(orderId)).Status);
    }

    /// <summary>
    /// A confirm retrying between the sweep's lookup and its void waits for the row, then loses:
    /// it never revives an authorisation being released. The next confirm pays under a fresh key.
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
        var reconciler = Reconciler(gateway);

        var sweep = reconciler.ReconcileBatchAsync(CancellationToken.None);
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

    [Fact]
    public async Task Reconcile_WhenACrashLeftAnAttemptPending_ShouldReleaseWhatTheGatewayHeld()
    {
        var (orderId, clientId) = NewOrder();
        var gateway = Gateway(lostRequestRate: 0);

        await CrashedAttemptAsync(gateway, orderId, clientId, landed: true);

        gateway.Options.TimeoutRate = 0;
        var reconciler = Reconciler(gateway);

        Assert.Equal(1, await reconciler.ReconcileBatchAsync(CancellationToken.None));

        var payment = await ReadAsync(orderId);
        Assert.Equal(PaymentStatus.Voided, payment.Status);
        Assert.NotNull(payment.GatewayReference);
        Assert.False(payment.IsLive);
    }

    [Fact]
    public async Task Reconcile_WhenACrashLeftAnAttemptPendingThatNeverArrived_ShouldAbandonIt()
    {
        var (orderId, clientId) = NewOrder();
        var gateway = Gateway();

        await CrashedAttemptAsync(gateway, orderId, clientId, landed: false);

        gateway.Options.TimeoutRate = 0;
        var reconciler = Reconciler(gateway);

        Assert.Equal(1, await reconciler.ReconcileBatchAsync(CancellationToken.None));
        Assert.Equal(PaymentStatus.Abandoned, (await ReadAsync(orderId)).Status);
    }

    [Fact]
    public async Task Reconcile_WhenACrashedAttemptsLookupGetsNoAnswer_ShouldRecordItAsTimedOut()
    {
        var (orderId, clientId) = NewOrder();
        var gateway = Gateway();

        await CrashedAttemptAsync(gateway, orderId, clientId, landed: false);

        var reconciler = Reconciler(gateway);

        Assert.Equal(0, await reconciler.ReconcileBatchAsync(CancellationToken.None));

        var payment = await ReadAsync(orderId);
        Assert.Equal(PaymentStatus.TimedOut, payment.Status);
        Assert.Equal(Afterwards, payment.ResolvedAt);
    }

    [Fact]
    public async Task Reconcile_WhenAPendingAttemptIsRecent_ShouldLeaveItToItsConfirm()
    {
        var (orderId, clientId) = NewOrder();
        var gateway = Gateway();

        await CrashedAttemptAsync(gateway, orderId, clientId, landed: false);

        gateway.Options.TimeoutRate = 0;
        var reconciler = Reconciler(gateway, at: T0.AddMinutes(1));

        Assert.Equal(0, await reconciler.ReconcileBatchAsync(CancellationToken.None));
        Assert.Equal(PaymentStatus.Pending, (await ReadAsync(orderId)).Status);
    }

    // -- Helpers ----------------------------------------------------------

    private static (Guid OrderId, Guid ClientId) NewOrder() => (Guid.NewGuid(), Guid.NewGuid());

    // As a crash leaves it: recorded Pending, never answered. Landed: the gateway did decide it.
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

    private PaymentReconciler Reconciler(
        TestGateway gateway,
        DateTime? at = null,
        Action<PaymentReconciliationOptions>? configure = null)
    {
        var options = new PaymentReconciliationOptions();
        configure?.Invoke(options);

        return new PaymentReconciler(
            _scopes,
            gateway.Gateway,
            Options.Create(options),
            new FakeTimeProvider(at ?? Afterwards),
            NullLogger<PaymentReconciler>.Instance);
    }

    private async Task<Payment> ReadAsync(Guid orderId)
    {
        await using var context = new PaymentsDbContext(_options);

        return await context.Payments
            .AsNoTracking()
            .SingleAsync(payment => payment.OrderId == orderId);
    }

    // Unordered: both rows share an AttemptedAt, so tests assert on the set.
    private async Task<IReadOnlyList<Payment>> ReadAllAsync(Guid orderId)
    {
        await using var context = new PaymentsDbContext(_options);

        return await context.Payments
            .AsNoTracking()
            .Where(payment => payment.OrderId == orderId)
            .ToListAsync();
    }

    private sealed record TestGateway(SimulatedPaymentGateway Gateway, PaymentSimulationOptions Options);
}
