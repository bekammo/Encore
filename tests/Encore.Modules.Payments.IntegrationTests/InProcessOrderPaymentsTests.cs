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
/// The in-process adapter end to end against real Postgres and a misbehaving gateway. Needs a
/// database because the one-live-attempt rule is a partial unique index. Fresh order ids per test.
/// </summary>
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

    /// <summary>How the gateway reaches its ledger table.</summary>
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

    /// <summary>The row exists even though the gateway never answered: it is written first.</summary>
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

    /// <summary>A retry after a timeout reuses the row and its key.</summary>
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

    /// <summary>Authorising again while authorised returns the existing hold.</summary>
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

    /// <summary>After a decline, a new attempt gets a new row and key.</summary>
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
    /// Another client asking about the same order sees nothing and is refused by the index.
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

    /// <summary>A capture with no answer changes nothing, so it can be retried.</summary>
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

    /// <summary>Voiding with nothing held is not a failure.</summary>
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

    /// <summary>A void with no answer leaves the hold recorded; it lapses at the gateway.</summary>
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

    /// <summary>A fresh adapter over a fresh context: each call stands for a separate request.</summary>
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

    /// <summary>The one attempt against this order; a second would break the reuse-the-row rule.</summary>
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
