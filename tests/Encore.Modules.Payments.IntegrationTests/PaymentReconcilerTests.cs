using Encore.Modules.Payments.Contracts;
using Encore.Modules.Payments.Data;
using Encore.Modules.Payments.Models;
using Encore.Modules.Payments.Simulation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Payments.IntegrationTests;

/// <summary>
/// The sweep that settles attempts the gateway never answered: what it resolves
/// them to, what it refuses to touch, and the thing it gives back to the customer
/// — an order that can be paid for again.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every timed-out row here is produced by the real adapter against the real
/// gateway</b>, rather than written into the table by hand. The ambiguity this class
/// resolves only exists because of how the row got there — the gateway either did or
/// did not receive a call it never answered — and a hand-seeded row would have no
/// truth behind it for a lookup to find.
/// </para>
/// <para>
/// <b>One gateway instance, two sets of weather.</b> The options object is mutated
/// between the two calls — timing out for the authorisation, answering for the
/// lookup — because that is the only difference these tests want between them.
/// </para>
/// <para>
/// <b>It no longer has to be the same instance, and that is <c>DECISIONS.md</c>
/// 066.</b> This remark used to say that rebuilding the gateway in between would
/// leave the lookup asking a stranger, because what it had decided lived in a
/// dictionary on the object. It lives in <c>payments.gateway_ledger</c> now, so a
/// second instance — or a second process, or the same one after a restart — gives the
/// same answer. <c>SimulatedPaymentGatewayTests</c> asserts exactly that, and 064's
/// first fault is what proved it needed asserting.
/// </para>
/// <para>
/// <b>One sweep is driven directly rather than by starting the hosted service</b>,
/// for the reason <c>OutboxDispatcherTests</c> gives: a test that started it and
/// waited would be timing-dependent, and its failures would be indistinguishable
/// from the bug it exists to catch.
/// </para>
/// <para>
/// <b>One branch is not covered here and it is worth naming.</b> A lookup that finds
/// funds held and then fails to release them leaves the row timed out for the next
/// sweep. Reaching it needs the lookup to answer and the void not to, and both are
/// governed by the one <c>TimeoutRate</c> knob — so forcing it would mean adding a
/// knob to the simulator that exists only to be a test's seam.
/// </para>
/// </remarks>
public sealed class PaymentReconcilerTests : IAsyncLifetime
{
    private const decimal Amount = 120.50m;
    private const string Currency = "GBP";

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Comfortably past <see cref="PaymentReconciliationOptions.MinimumAge"/>.</summary>
    private static readonly DateTime Afterwards = T0.AddMinutes(10);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("encore")
        .WithUsername("encore")
        .WithPassword("encore")
        .Build();

    private string _connectionString = null!;
    private DbContextOptions<PaymentsDbContext> _options = null!;

    /// <summary>
    /// How the gateway reaches its ledger, which is a table since
    /// <c>DECISIONS.md</c> 066 rather than a field on the instance.
    /// </summary>
    private ServiceProvider _provider = null!;
    private IServiceScopeFactory _scopes = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();

        _options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UsePaymentsNpgsql(_connectionString)
            .Options;

        await using var context = new PaymentsDbContext(_options);
        await context.Database.MigrateAsync();

        var services = new ServiceCollection();
        services.AddDbContext<PaymentsDbContext>(builder => builder.UsePaymentsNpgsql(_connectionString));

        _provider = services.BuildServiceProvider();
        _scopes = _provider.GetRequiredService<IServiceScopeFactory>();
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    /// <summary>
    /// The branch that actually returns somebody's money. The authorisation reached
    /// the gateway and the answer was lost coming back, so funds really are held —
    /// and the order they were held for was abandoned at the same moment, because a
    /// confirm whose authorisation times out never gets as far as selling a seat.
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

    /// <summary>
    /// The other half of the ambiguity: the request never arrived, so nothing was
    /// ever held and there is nothing to release.
    /// </summary>
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

    /// <summary>
    /// A refusal whose answer was lost. This is the only way a timed-out attempt
    /// ends up declined, and the distinction matters: <c>DECISIONS.md</c> 031
    /// refused to read a silence as a refusal, and this is not that — the gateway
    /// said no, and the sweep went and read it.
    /// </summary>
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

    /// <summary>
    /// A lookup that gets no answer has established nothing. Writing anything here
    /// would be the sweep inventing the fact it came to find — and the cheap
    /// invention, "nothing happened", is the one that would hand the order its slot
    /// back while the customer's funds were still held.
    /// </summary>
    [Fact]
    public async Task Reconcile_WhenTheLookupGetsNoAnswer_ShouldLeaveTheAttemptUnresolved()
    {
        var (orderId, clientId) = NewOrder();
        var gateway = Gateway(lostRequestRate: 0);

        await AuthorizeAsync(gateway, orderId, clientId);

        // TimeoutRate stays at 1, so the lookup hangs up exactly as the authorisation did.
        await using var host = Host(gateway);

        Assert.Equal(0, await host.Reconciler.ReconcileBatchAsync(CancellationToken.None));

        var payment = await ReadAsync(orderId);
        Assert.Equal(PaymentStatus.TimedOut, payment.Status);
        Assert.True(payment.IsLive);
    }

    /// <summary>
    /// Younger than <see cref="PaymentReconciliationOptions.MinimumAge"/>, so the
    /// customer may still be on the confirm that will retry it — and that confirm
    /// has somebody waiting on the answer, which this does not.
    /// </summary>
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

    /// <summary>
    /// Only the ambiguity of a timeout licenses settling an attempt on an answer
    /// nobody was told. An authorisation that was heard is the request path's
    /// business and stays that way.
    /// </summary>
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

    /// <summary>
    /// The payoff. A timed-out attempt holds the order's one live slot, so until it
    /// is settled the customer cannot pay for this order at all — not with a
    /// different card, not tomorrow. Resolving it is what gives that back.
    /// </summary>
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

        // Two rows and two keys: the settled one was not reopened. Reusing a row is
        // only for the ambiguity of a timeout, and this order no longer has one.
        Assert.Equal(2, attempts.Count);
        Assert.Equal(2, attempts.Select(attempt => attempt.IdempotencyKey).Distinct().Count());
        Assert.Single(attempts, attempt => attempt.Status is PaymentStatus.Abandoned);

        var live = Assert.Single(attempts, attempt => attempt.IsLive);
        Assert.Equal(PaymentStatus.Authorized, live.Status);
    }

    /// <summary>
    /// A settled attempt is out of the candidate set for good, so a second sweep
    /// finds nothing to do and the gateway is not asked again about a question that
    /// already has an answer.
    /// </summary>
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

    /// <summary>
    /// The batch is a ceiling on gateway round trips per sweep, not a promise about
    /// how much there is to do. What it does not settle this time is first in line
    /// next time, because the sweep takes the oldest attempts first.
    /// </summary>
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
    /// The lease 073 asked for and this class's own remarks now describe: two
    /// instances must not both be mid-sweep. The "other instance" here is just
    /// another connection holding the same advisory lock in an open transaction —
    /// which is exactly what a second <c>PaymentReconciler</c> process would look
    /// like from this one's point of view, and cheaper than actually running one.
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

        // The holder's transaction above released the lease when it ended, so
        // this sweep — otherwise identical to the one the lease just refused —
        // gets it and does the work the first one could not.
        Assert.Equal(1, await host.Reconciler.ReconcileBatchAsync(CancellationToken.None));
        Assert.Equal(PaymentStatus.Abandoned, (await ReadAsync(orderId)).Status);
    }

    // -- Scaffolding ------------------------------------------------------

    private static (Guid OrderId, Guid ClientId) NewOrder() => (Guid.NewGuid(), Guid.NewGuid());

    /// <summary>
    /// A gateway that hangs up on every call, paired with the options object it is
    /// still reading so a test can stop it hanging up later.
    /// </summary>
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

        var payments = new InProcessOrderPayments(context, gateway.Gateway, new FixedTimeProvider(T0));

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

        services.AddDbContext<PaymentsDbContext>(builder => builder.UsePaymentsNpgsql(_connectionString));

        var provider = services.BuildServiceProvider();

        var reconciler = new PaymentReconciler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            gateway.Gateway,
            Options.Create(options),
            new FixedTimeProvider(at ?? Afterwards),
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
    /// Every attempt against this order, in no particular order — the one test that
    /// reads more than one row asserts on what is in the set rather than on its
    /// sequence, because both rows carry the same <c>AttemptedAt</c> and there is
    /// nothing honest to sort them by.
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

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
