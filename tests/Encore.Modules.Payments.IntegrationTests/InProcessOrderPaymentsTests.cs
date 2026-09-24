using Encore.Modules.Payments.Contracts;
using Encore.Modules.Payments.Data;
using Encore.Modules.Payments.Models;
using Encore.Modules.Payments.Simulation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Encore.Modules.Payments.IntegrationTests;

/// <summary>The database is never emptied, so every test uses fresh order ids.</summary>
public sealed class InProcessOrderPaymentsTests(PaymentsDatabase database) : IClassFixture<PaymentsDatabase>
{
    private const decimal Amount = 120.50m;
    private const string Currency = "GBP";

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly DbContextOptions<PaymentsDbContext> _options = database.Options;

    private readonly IServiceScopeFactory _scopes = database.Scopes;

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

    /// <summary>The second confirm lands while the first is still waiting on the gateway, under the same key.</summary>
    [Fact]
    public async Task Authorize_WhileAnotherConfirmIsAskingTheGateway_ShouldAnswerRatherThanFail()
    {
        var (orderId, clientId) = NewOrder();
        var slow = TimeSpan.FromSeconds(1);

        var first = Payments(latency: slow).AuthorizeAsync(Authorize(orderId, clientId));
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        var second = await Payments(latency: slow).AuthorizeAsync(Authorize(orderId, clientId));
        var answers = new[] { (await first).Status, second.Status };

        Assert.Contains(AuthorizePaymentStatus.Authorized, answers);
        Assert.All(answers, answer => Assert.True(
            answer is AuthorizePaymentStatus.Authorized or AuthorizePaymentStatus.ConcurrentAttemptInFlight,
            $"Unexpected answer {answer}."));

        Assert.Equal(PaymentStatus.Authorized, (await ReadAsync(orderId)).Status);
    }

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

    /// <summary>Another client's lookup finds nothing, so its insert is refused by the index.</summary>
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

    // A fresh adapter over a fresh context: each call stands for a separate request.
    private IOrderPayments Payments(double declineRate = 0, double timeoutRate = 0, TimeSpan? latency = null) =>
        new InProcessOrderPayments(
            new PaymentsDbContext(_options),
            new SimulatedPaymentGateway(
                _scopes,
                Options.Create(new PaymentSimulationOptions
                {
                    DeclineRate = declineRate,
                    TimeoutRate = timeoutRate,
                    MinLatency = latency ?? TimeSpan.Zero,
                    MaxLatency = latency ?? TimeSpan.Zero
                }),
                TimeProvider.System),
            new FakeTimeProvider(T0));

    // SingleAsync on purpose: a second row would break the reuse-the-row rule.
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
}
