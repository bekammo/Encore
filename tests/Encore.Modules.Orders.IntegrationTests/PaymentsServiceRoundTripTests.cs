using System.Globalization;
using Encore.Modules.Orders.Data;
using Encore.Modules.Payments;
using Encore.Modules.Payments.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Encore.Modules.Orders.IntegrationTests;

/// <summary>
/// Every other test of this seam fakes one side; only here do both run, so a wire format they
/// disagree on fails (018). The database is never emptied, so every test uses fresh order ids.
/// </summary>
public sealed class PaymentsServiceRoundTripTests(OrdersDatabase database)
    : IClassFixture<OrdersDatabase>, IAsyncLifetime
{
    private const string Token = "round-trip-token";

    private readonly OrdersDatabase _database = database;

    private readonly List<WebApplication> _services = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var service in _services)
        {
            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task EveryAnswerOfAnOrdersLife_ShouldCrossTheWireIntact()
    {
        var payments = await ClientAsync();
        var (orderId, clientId) = (Guid.NewGuid(), Guid.NewGuid());

        var authorized = await payments.AuthorizeAsync(new AuthorizePaymentRequest(orderId, clientId, 25m, "GBP"));
        Assert.Equal(AuthorizePaymentStatus.Authorized, authorized.Status);
        Assert.NotNull(authorized.PaymentId);

        var again = await payments.AuthorizeAsync(new AuthorizePaymentRequest(orderId, clientId, 25m, "GBP"));
        Assert.Equal(AuthorizePaymentStatus.Authorized, again.Status);
        Assert.Equal(authorized.PaymentId, again.PaymentId);

        var captured = await payments.CaptureAsync(new CapturePaymentRequest(orderId, clientId));
        Assert.Equal(CapturePaymentStatus.Captured, captured.Status);

        var voided = await payments.VoidAsync(new VoidPaymentRequest(orderId, clientId));
        Assert.Equal(VoidPaymentStatus.AlreadyCaptured, voided.Status);

        var reauthorized = await payments.AuthorizeAsync(new AuthorizePaymentRequest(orderId, clientId, 25m, "GBP"));
        Assert.Equal(AuthorizePaymentStatus.AlreadyCaptured, reauthorized.Status);
    }

    [Fact]
    public async Task AVoidAndNothingHeld_ShouldCrossTheWireIntact()
    {
        var payments = await ClientAsync();
        var (orderId, clientId) = (Guid.NewGuid(), Guid.NewGuid());

        await payments.AuthorizeAsync(new AuthorizePaymentRequest(orderId, clientId, 25m, "GBP"));

        Assert.Equal(
            VoidPaymentStatus.Voided,
            (await payments.VoidAsync(new VoidPaymentRequest(orderId, clientId))).Status);
        Assert.Equal(
            VoidPaymentStatus.NoAuthorization,
            (await payments.VoidAsync(new VoidPaymentRequest(orderId, clientId))).Status);
        Assert.Equal(
            CapturePaymentStatus.NoAuthorization,
            (await payments.CaptureAsync(new CapturePaymentRequest(orderId, clientId))).Status);
    }

    [Fact]
    public async Task ADecline_ShouldCrossTheWireIntact()
    {
        var payments = await ClientAsync(declineRate: 1);

        var declined = await payments.AuthorizeAsync(
            new AuthorizePaymentRequest(Guid.NewGuid(), Guid.NewGuid(), 25m, "GBP"));

        Assert.Equal(AuthorizePaymentStatus.Declined, declined.Status);
        Assert.NotNull(declined.PaymentId);
    }

    /// <summary>A real timeout names its attempt; the unreadable-answer fallback carries Guid.Empty.</summary>
    [Fact]
    public async Task AGatewayTimeout_ShouldCrossTheWireIntact()
    {
        var payments = await ClientAsync(timeoutRate: 1);

        var timedOut = await payments.AuthorizeAsync(
            new AuthorizePaymentRequest(Guid.NewGuid(), Guid.NewGuid(), 25m, "GBP"));

        Assert.Equal(AuthorizePaymentStatus.TimedOut, timedOut.Status);
        Assert.NotEqual(Guid.Empty, timedOut.PaymentId);
    }

    /// <summary>Read as a timeout, it would leave a sold order waiting on money nobody holds.</summary>
    [Fact]
    public async Task ARefusedCapture_ShouldCrossTheWireIntact()
    {
        var payments = await ClientAsync(captureDeclineRate: 1);
        var (orderId, clientId) = (Guid.NewGuid(), Guid.NewGuid());

        var authorized = await payments.AuthorizeAsync(new AuthorizePaymentRequest(orderId, clientId, 25m, "GBP"));
        var refused = await payments.CaptureAsync(new CapturePaymentRequest(orderId, clientId));

        Assert.Equal(CapturePaymentStatus.Declined, refused.Status);
        Assert.Equal(authorized.PaymentId, refused.PaymentId);
    }

    private async Task<HttpOrderPayments> ClientAsync(
        double declineRate = 0,
        double timeoutRate = 0,
        double captureDeclineRate = 0)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Payments"] = _database.ConnectionString,
            ["Payments:ServiceToken"] = Token,
            ["Payments:Reconciliation:Enabled"] = "false",
            ["Payments:Simulation:DeclineRate"] = declineRate.ToString(CultureInfo.InvariantCulture),
            ["Payments:Simulation:TimeoutRate"] = timeoutRate.ToString(CultureInfo.InvariantCulture),
            ["Payments:Simulation:CaptureDeclineRate"] = captureDeclineRate.ToString(CultureInfo.InvariantCulture),
            ["Payments:Simulation:LostRequestRate"] = "0",
            ["Payments:Simulation:MinLatency"] = "00:00:00",
            ["Payments:Simulation:MaxLatency"] = "00:00:00"
        });

        builder.Services.AddPaymentsModule(builder.Configuration);

        var service = builder.Build();
        service.MapPaymentsServiceApi(builder.Configuration);

        await service.StartAsync();
        _services.Add(service);

        var address = service.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .Single();

        var http = new HttpClient { BaseAddress = new Uri(address.TrimEnd('/') + "/internal/payments/") };
        http.DefaultRequestHeaders.Add(PaymentsServiceApi.ServiceTokenHeader, Token);

        return new HttpOrderPayments(http);
    }
}
