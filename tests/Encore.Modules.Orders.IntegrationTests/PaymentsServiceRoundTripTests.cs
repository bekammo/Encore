using Encore.Modules.Orders.Data;
using Encore.Modules.Payments;
using Encore.Modules.Payments.Contracts;
using Encore.Modules.Payments.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Orders.IntegrationTests;

/// <summary>
/// Orders' HTTP client against the real Payments service routes, hosted in process on a real
/// port. Every other test of this seam fakes one side: <c>HttpOrderPaymentsTests</c> answers
/// from a stub, and <c>PaymentServiceEndpointsTests</c> sends hand-written requests. Only here
/// do both run together, so a wire format the two disagree on fails a test (018).
/// </summary>
public sealed class PaymentsServiceRoundTripTests : IAsyncLifetime
{
    private const string Token = "round-trip-token";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("encore")
        .WithUsername("encore")
        .WithPassword("encore")
        .Build();

    private readonly List<WebApplication> _services = [];

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        await using var context = new PaymentsDbContext(
            new DbContextOptionsBuilder<PaymentsDbContext>().UsePaymentsNpgsql(_postgres.GetConnectionString()).Options);

        await context.Database.MigrateAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        foreach (var service in _services)
        {
            await service.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }

    /// <summary>Authorise, authorise again, capture, then a void that finds the money taken.</summary>
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

    /// <summary>An authorisation released by a void, and the answers when nothing is held.</summary>
    [Fact]
    public async Task AVoidAndNothingHeld_ShouldCrossTheWireIntact()
    {
        var payments = await ClientAsync();
        var (orderId, clientId) = (Guid.NewGuid(), Guid.NewGuid());

        await payments.AuthorizeAsync(new AuthorizePaymentRequest(orderId, clientId, 25m, "GBP"));

        Assert.Equal(VoidPaymentStatus.Voided, (await payments.VoidAsync(new VoidPaymentRequest(orderId, clientId))).Status);
        Assert.Equal(VoidPaymentStatus.NoAuthorization, (await payments.VoidAsync(new VoidPaymentRequest(orderId, clientId))).Status);
        Assert.Equal(CapturePaymentStatus.NoAuthorization, (await payments.CaptureAsync(new CapturePaymentRequest(orderId, clientId))).Status);
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

    /// <summary>A gateway timeout arrives as one, not as the unreadable-answer fallback.</summary>
    [Fact]
    public async Task AGatewayTimeout_ShouldCrossTheWireIntact()
    {
        var payments = await ClientAsync(timeoutRate: 1);

        var timedOut = await payments.AuthorizeAsync(
            new AuthorizePaymentRequest(Guid.NewGuid(), Guid.NewGuid(), 25m, "GBP"));

        Assert.Equal(AuthorizePaymentStatus.TimedOut, timedOut.Status);
        Assert.NotEqual(Guid.Empty, timedOut.PaymentId);
    }

    /// <summary>
    /// The Payments service as <c>Encore.Payments.Api</c> composes it, on a port of its own, and
    /// Orders' client pointed at it.
    /// </summary>
    private async Task<HttpOrderPayments> ClientAsync(double declineRate = 0, double timeoutRate = 0)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Payments"] = _postgres.GetConnectionString(),
            ["Payments:ServiceToken"] = Token,
            ["Payments:Reconciliation:Enabled"] = "false",
            ["Payments:Simulation:DeclineRate"] = declineRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Payments:Simulation:TimeoutRate"] = timeoutRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
            .Features.GetRequiredFeature<IServerAddressesFeature>()
            .Addresses.First();

        var http = new HttpClient { BaseAddress = new Uri(address + "/internal/payments/") };
        http.DefaultRequestHeaders.Add(PaymentsServiceApi.ServiceTokenHeader, Token);

        return new HttpOrderPayments(http);
    }
}
