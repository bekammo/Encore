using Encore.Modules.Payments.Contracts;
using Encore.Modules.Payments.Data;
using Encore.Modules.Payments.Models;
using Encore.Modules.Payments.Simulation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Payments.IntegrationTests;

/// <summary>
/// The module's public face, driven end to end against real Postgres and a
/// gateway configured to behave badly on demand.
/// </summary>
/// <remarks>
/// <para>
/// These need a database because the rule that makes the whole design safe — one
/// live attempt per order — is a partial unique index, and the adapter catches its
/// violation by name. A fake would fake exactly that away.
/// </para>
/// <para>
/// Every test uses a fresh order id, because the container is per-class and rows
/// accumulate against that index.
/// </para>
/// </remarks>
public sealed class InProcessOrderPaymentsTests : IAsyncLifetime
{
    private const decimal Amount = 120.50m;
    private const string Currency = "GBP";

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("encore")
        .WithUsername("encore")
        .WithPassword("encore")
        .Build();

    private DbContextOptions<PaymentsDbContext> _options = null!;

    /// <summary>
    /// How the gateway reaches its ledger. Since <c>DECISIONS.md</c> 066 what it has
    /// already answered is a row rather than a field, so a gateway needs a way to
    /// open a context of its own.
    /// </summary>
    private ServiceProvider _provider = null!;
    private IServiceScopeFactory _scopes = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var connectionString = _postgres.GetConnectionString();

        _options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UsePaymentsNpgsql(connectionString)
            .Options;

        await using var context = new PaymentsDbContext(_options);
        await context.Database.MigrateAsync();

        var services = new ServiceCollection();
        services.AddDbContext<PaymentsDbContext>(builder => builder.UsePaymentsNpgsql(connectionString));

        _provider = services.BuildServiceProvider();
        _scopes = _provider.GetRequiredService<IServiceScopeFactory>();
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    // -- Authorize --------------------------------------------------------

    [Fact]
    public async Task Authorize_WhenTheGatewayAgrees_ShouldHoldTheFunds()
    {
        var (orderId, clientId) = NewOrder();

        var response = await Payments().AuthorizeAsync(Authorize(orderId, clientId));

        Assert.Equal(AuthorizePaymentStatus.Authorized, response.Status);

        var payment = await ReadAsync(orderId);
        Assert.Equal(PaymentStatus.Authorized, payment.Status);
        Assert.NotNull(payment.GatewayReference);
    }

    [Fact]
    public async Task Authorize_WhenTheGatewayRefuses_ShouldRecordADecline()
    {
        var (orderId, clientId) = NewOrder();

        var response = await Payments(declineRate: 1).AuthorizeAsync(Authorize(orderId, clientId));

        Assert.Equal(AuthorizePaymentStatus.Declined, response.Status);

        var payment = await ReadAsync(orderId);
        Assert.Equal(PaymentStatus.Declined, payment.Status);
        Assert.False(payment.IsLive);
    }

    /// <summary>
    /// The row exists even though the gateway never answered, and that is the
    /// point: it is written before the call, so a crash mid-flight leaves
    /// something for the retry to find. See <c>DECISIONS.md</c> 031.
    /// </summary>
    [Fact]
    public async Task Authorize_WhenTheGatewayNeverAnswers_ShouldLeaveALiveAttemptBehind()
    {
        var (orderId, clientId) = NewOrder();

        var response = await Payments(timeoutRate: 1).AuthorizeAsync(Authorize(orderId, clientId));

        Assert.Equal(AuthorizePaymentStatus.TimedOut, response.Status);

        var payment = await ReadAsync(orderId);
        Assert.Equal(PaymentStatus.TimedOut, payment.Status);
        Assert.True(payment.IsLive);
    }

    /// <summary>
    /// The load-bearing test for the retry path. A second authorisation after a
    /// timeout must reuse the row, and therefore the key — a new key against a
    /// gateway that did receive the first call is a second hold on the customer's
    /// money.
    /// </summary>
    [Fact]
    public async Task Authorize_AfterATimeout_ShouldReuseTheRowAndTheKey()
    {
        var (orderId, clientId) = NewOrder();

        await Payments(timeoutRate: 1).AuthorizeAsync(Authorize(orderId, clientId));
        var timedOut = await ReadAsync(orderId);

        var response = await Payments().AuthorizeAsync(Authorize(orderId, clientId));

        Assert.Equal(AuthorizePaymentStatus.Authorized, response.Status);

        var retried = await ReadAsync(orderId);
        Assert.Equal(timedOut.Id, retried.Id);
        Assert.Equal(timedOut.IdempotencyKey, retried.IdempotencyKey);
        Assert.Equal(1, await CountAsync(orderId));
    }

    /// <summary>
    /// Idempotent, for the reason re-holding a seat you already hold is: a retry
    /// after a dropped response must get back what it already has rather than a
    /// second hold.
    /// </summary>
    [Fact]
    public async Task Authorize_WhenAlreadyAuthorized_ShouldReturnTheSameAttempt()
    {
        var (orderId, clientId) = NewOrder();

        var first = await Payments().AuthorizeAsync(Authorize(orderId, clientId));
        var again = await Payments().AuthorizeAsync(Authorize(orderId, clientId));

        Assert.Equal(AuthorizePaymentStatus.Authorized, again.Status);
        Assert.Equal(first.PaymentId, again.PaymentId);
        Assert.Equal(1, await CountAsync(orderId));
    }

    [Fact]
    public async Task Authorize_WhenAlreadyPaid_ShouldSaySoRatherThanChargeAgain()
    {
        var (orderId, clientId) = NewOrder();

        await Payments().AuthorizeAsync(Authorize(orderId, clientId));
        await Payments().CaptureAsync(new CapturePaymentRequest(orderId, clientId));

        var response = await Payments().AuthorizeAsync(Authorize(orderId, clientId));

        Assert.Equal(AuthorizePaymentStatus.AlreadyCaptured, response.Status);
        Assert.Equal(1, await CountAsync(orderId));
    }

    /// <summary>
    /// A declined attempt moved nothing, so the customer may try again — and that
    /// is a genuinely new attempt with a new key, not a reuse of the old row.
    /// </summary>
    [Fact]
    public async Task Authorize_AfterADecline_ShouldStartAFreshAttempt()
    {
        var (orderId, clientId) = NewOrder();

        var declined = await Payments(declineRate: 1).AuthorizeAsync(Authorize(orderId, clientId));
        var retried = await Payments().AuthorizeAsync(Authorize(orderId, clientId));

        Assert.Equal(AuthorizePaymentStatus.Authorized, retried.Status);
        Assert.NotEqual(declined.PaymentId, retried.PaymentId);
        Assert.Equal(2, await CountAsync(orderId));
    }

    /// <summary>
    /// A payment belongs to the client who is paying. Another client asking about
    /// the same order gets nothing back and starts its own attempt — which the
    /// live-attempt index then refuses, because the order already has one.
    /// </summary>
    [Fact]
    public async Task Authorize_ForSomebodyElsesOrder_ShouldNotFindTheExistingAttempt()
    {
        var (orderId, clientId) = NewOrder();

        await Payments().AuthorizeAsync(Authorize(orderId, clientId));

        var response = await Payments().AuthorizeAsync(Authorize(orderId, Guid.NewGuid()));

        Assert.Equal(AuthorizePaymentStatus.ConcurrentAttemptInFlight, response.Status);
        Assert.Equal(1, await CountAsync(orderId));
    }

    // -- Capture ----------------------------------------------------------

    [Fact]
    public async Task Capture_WhenFundsAreHeld_ShouldTakeThem()
    {
        var (orderId, clientId) = NewOrder();
        await Payments().AuthorizeAsync(Authorize(orderId, clientId));

        var response = await Payments().CaptureAsync(new CapturePaymentRequest(orderId, clientId));

        Assert.Equal(CapturePaymentStatus.Captured, response.Status);
        Assert.Equal(PaymentStatus.Captured, (await ReadAsync(orderId)).Status);
    }

    [Fact]
    public async Task Capture_WhenAlreadyCaptured_ShouldSaySoRatherThanFail()
    {
        var (orderId, clientId) = NewOrder();
        await Payments().AuthorizeAsync(Authorize(orderId, clientId));
        await Payments().CaptureAsync(new CapturePaymentRequest(orderId, clientId));

        var response = await Payments().CaptureAsync(new CapturePaymentRequest(orderId, clientId));

        Assert.Equal(CapturePaymentStatus.Captured, response.Status);
    }

    [Fact]
    public async Task Capture_WithNothingHeld_ShouldSayThereIsNoAuthorization()
    {
        var (orderId, clientId) = NewOrder();

        var response = await Payments().CaptureAsync(new CapturePaymentRequest(orderId, clientId));

        Assert.Equal(CapturePaymentStatus.NoAuthorization, response.Status);
    }

    /// <summary>
    /// A capture that gets no answer changes nothing on purpose. The funds are
    /// still held and the reference is still there, so the next attempt can ask
    /// again — writing a timeout onto the row would throw away the only thing that
    /// makes the retry possible.
    /// </summary>
    [Fact]
    public async Task Capture_WhenTheGatewayNeverAnswers_ShouldLeaveTheHoldCapturable()
    {
        var (orderId, clientId) = NewOrder();
        await Payments().AuthorizeAsync(Authorize(orderId, clientId));

        var response = await Payments(timeoutRate: 1)
            .CaptureAsync(new CapturePaymentRequest(orderId, clientId));

        Assert.Equal(CapturePaymentStatus.TimedOut, response.Status);

        var payment = await ReadAsync(orderId);
        Assert.Equal(PaymentStatus.Authorized, payment.Status);
        Assert.NotNull(payment.GatewayReference);

        var retried = await Payments().CaptureAsync(new CapturePaymentRequest(orderId, clientId));
        Assert.Equal(CapturePaymentStatus.Captured, retried.Status);
    }

    // -- Void -------------------------------------------------------------

    [Fact]
    public async Task Void_WhenFundsAreHeld_ShouldReleaseThem()
    {
        var (orderId, clientId) = NewOrder();
        await Payments().AuthorizeAsync(Authorize(orderId, clientId));

        var response = await Payments().VoidAsync(new VoidPaymentRequest(orderId, clientId));

        Assert.Equal(VoidPaymentStatus.Voided, response.Status);

        var payment = await ReadAsync(orderId);
        Assert.Equal(PaymentStatus.Voided, payment.Status);
        Assert.False(payment.IsLive);
    }

    [Fact]
    public async Task Void_WhenAlreadyCaptured_ShouldRefuseRatherThanPretendToRefund()
    {
        var (orderId, clientId) = NewOrder();
        await Payments().AuthorizeAsync(Authorize(orderId, clientId));
        await Payments().CaptureAsync(new CapturePaymentRequest(orderId, clientId));

        var response = await Payments().VoidAsync(new VoidPaymentRequest(orderId, clientId));

        Assert.Equal(VoidPaymentStatus.AlreadyCaptured, response.Status);
        Assert.Equal(PaymentStatus.Captured, (await ReadAsync(orderId)).Status);
    }

    /// <summary>
    /// Nothing to unwind is not a failure. A caller cleaning up after a confirm
    /// that never got as far as authorising must be able to call this blindly.
    /// </summary>
    [Fact]
    public async Task Void_WithNothingHeld_ShouldSayThereIsNoAuthorization()
    {
        var (orderId, clientId) = NewOrder();

        var response = await Payments().VoidAsync(new VoidPaymentRequest(orderId, clientId));

        Assert.Equal(VoidPaymentStatus.NoAuthorization, response.Status);
    }

    [Fact]
    public async Task Void_WhenAlreadyVoided_ShouldSayThereIsNothingToUnwind()
    {
        var (orderId, clientId) = NewOrder();
        await Payments().AuthorizeAsync(Authorize(orderId, clientId));
        await Payments().VoidAsync(new VoidPaymentRequest(orderId, clientId));

        var response = await Payments().VoidAsync(new VoidPaymentRequest(orderId, clientId));

        Assert.Equal(VoidPaymentStatus.NoAuthorization, response.Status);
    }

    /// <summary>
    /// A void that gets no answer leaves the hold recorded, and that is the benign
    /// half of the whole design: an authorisation nobody captures lapses at the
    /// gateway on its own, so the customer is never out of pocket.
    /// </summary>
    [Fact]
    public async Task Void_WhenTheGatewayNeverAnswers_ShouldLeaveTheHoldRecorded()
    {
        var (orderId, clientId) = NewOrder();
        await Payments().AuthorizeAsync(Authorize(orderId, clientId));

        var response = await Payments(timeoutRate: 1)
            .VoidAsync(new VoidPaymentRequest(orderId, clientId));

        Assert.Equal(VoidPaymentStatus.TimedOut, response.Status);
        Assert.Equal(PaymentStatus.Authorized, (await ReadAsync(orderId)).Status);
    }

    // -- Helpers ----------------------------------------------------------

    private static (Guid OrderId, Guid ClientId) NewOrder() => (Guid.NewGuid(), Guid.NewGuid());

    private static AuthorizePaymentRequest Authorize(Guid orderId, Guid clientId) =>
        new(orderId, clientId, Amount, Currency);

    /// <summary>
    /// A fresh adapter over a fresh context, because each call in these tests
    /// stands for a separate request.
    /// </summary>
    private IOrderPayments Payments(double declineRate = 0, double timeoutRate = 0) =>
        new InProcessOrderPayments(
            new PaymentsDbContext(_options),
            new SimulatedPaymentGateway(
                _scopes,
                Options.Create(new PaymentSimulationOptions
                {
                    DeclineRate = declineRate,
                    TimeoutRate = timeoutRate,
                    MinLatency = TimeSpan.Zero,
                    MaxLatency = TimeSpan.Zero
                }),
                TimeProvider.System),
            new FixedTimeProvider(T0));

    /// <summary>
    /// The one attempt against this order. <c>Single</c> rather than <c>First</c>
    /// on purpose: every caller here expects exactly one row, and a second would
    /// mean the reuse-the-row rule had quietly stopped holding.
    /// </summary>
    private async Task<Payment> ReadAsync(Guid orderId)
    {
        await using var context = new PaymentsDbContext(_options);

        return await context.Payments
            .AsNoTracking()
            .SingleAsync(payment => payment.OrderId == orderId);
    }

    private async Task<int> CountAsync(Guid orderId)
    {
        await using var context = new PaymentsDbContext(_options);

        return await context.Payments.CountAsync(payment => payment.OrderId == orderId);
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
