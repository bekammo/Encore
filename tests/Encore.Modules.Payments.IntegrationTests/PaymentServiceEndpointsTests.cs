using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Encore.Modules.Payments.Endpoints;
using Encore.Modules.Payments.Models;
using Encore.Modules.Payments.Simulation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Encore.Modules.Payments.IntegrationTests;

/// <summary>
/// The Payments service API over a real socket and real Postgres, composed as a host composes
/// it, so the token filter and the problem+json shape are part of what is tested.
/// <c>HttpOrderPaymentsTests</c> pins the reading side. The database is shared by the class
/// and never emptied, so every test pays for orders of its own; the service is started per test.
/// </summary>
public sealed class PaymentServiceEndpointsTests(PaymentsDatabase database)
    : IClassFixture<PaymentsDatabase>, IAsyncLifetime
{
    private const string Token = "a-token-for-this-test-only";

    private readonly PaymentsDatabase _database = database;

    private WebApplication _service = null!;
    private HttpClient _client = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Payments"] = _database.ConnectionString,
            // The reconciler would race the assertions.
            ["Payments:Reconciliation:Enabled"] = "false",
            // A gateway that always agrees; refusals are arranged through the row.
            ["Payments:Simulation:DeclineRate"] = "0",
            ["Payments:Simulation:TimeoutRate"] = "0",
            ["Payments:Simulation:MinLatency"] = "00:00:00",
            ["Payments:Simulation:MaxLatency"] = "00:00:00"
        });

        builder.Services.AddProblemDetails();
        builder.Services.AddPaymentsModule(builder.Configuration);

        _service = builder.Build();
        _service.UseExceptionHandler();
        _service.UseStatusCodePages();
        _service.MapPaymentServiceEndpoints(Token);

        await _service.StartAsync();

        var address = _service.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .Single();

        _client = new HttpClient { BaseAddress = new Uri(address.TrimEnd('/') + "/internal/payments/") };
        _client.DefaultRequestHeaders.Add(Contracts.PaymentsServiceApi.ServiceTokenHeader, Token);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _service.DisposeAsync();
    }

    // -- the service token ------------------------------------------------

    /// <summary>A caller without the token cannot reach the routes.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-the-token")]
    public async Task EveryRoute_WithoutTheToken_ShouldRefuse(string? token)
    {
        using var client = new HttpClient { BaseAddress = _client.BaseAddress };

        if (token is not null)
        {
            client.DefaultRequestHeaders.Add(Contracts.PaymentsServiceApi.ServiceTokenHeader, token);
        }

        foreach (var route in new[] { "authorize", "capture", "void" })
        {
            using var response = await client.PostAsJsonAsync(
                route, new { orderId = Guid.NewGuid(), clientId = Guid.NewGuid(), amount = 1m, currency = "GBP" });

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("service_token_invalid", problem.GetProperty("reason").GetString());
        }
    }

    /// <summary>A client id instead of the token gets 401.</summary>
    [Fact]
    public async Task Authorize_WithAClientIdInsteadOfTheToken_ShouldRefuse()
    {
        using var client = new HttpClient { BaseAddress = _client.BaseAddress };
        client.DefaultRequestHeaders.Add("X-Client-Id", Guid.NewGuid().ToString());

        using var response = await client.PostAsJsonAsync(
            "authorize", new { orderId = Guid.NewGuid(), clientId = Guid.NewGuid(), amount = 1m, currency = "GBP" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -- the outcomes the adapter reads -----------------------------------

    [Fact]
    public async Task Authorize_WhenTheGatewayAgrees_ShouldAnswerAuthorized()
    {
        var order = Guid.NewGuid();

        var (status, body) = await AuthorizeAsync(order);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("authorized", body.GetProperty("outcome").GetString());
        Assert.NotEqual(Guid.Empty, body.GetProperty("paymentId").GetGuid());
    }

    /// <summary>A repeated authorisation is an answer, not a second charge.</summary>
    [Fact]
    public async Task Authorize_Twice_ShouldReturnTheSameAttempt()
    {
        var order = Guid.NewGuid();

        var (_, first) = await AuthorizeAsync(order);
        var (status, second) = await AuthorizeAsync(order);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("authorized", second.GetProperty("outcome").GetString());
        Assert.Equal(first.GetProperty("paymentId").GetGuid(), second.GetProperty("paymentId").GetGuid());
    }

    [Fact]
    public async Task Capture_AfterAuthorizing_ShouldAnswerCaptured()
    {
        var order = Guid.NewGuid();
        await AuthorizeAsync(order);

        var (status, body) = await PostAsync("capture", order);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("captured", body.GetProperty("outcome").GetString());
    }

    /// <summary>Idempotent, and the adapter depends on it being so.</summary>
    [Fact]
    public async Task Capture_Twice_ShouldStillAnswerCaptured()
    {
        var order = Guid.NewGuid();
        await AuthorizeAsync(order);
        await PostAsync("capture", order);

        var (status, body) = await PostAsync("capture", order);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("captured", body.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Capture_WithNothingHeld_ShouldRefuseWithNoAuthorization()
    {
        var (status, body) = await PostAsync("capture", Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("no_authorization", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Void_AfterAuthorizing_ShouldAnswerVoided()
    {
        var order = Guid.NewGuid();
        await AuthorizeAsync(order);

        var (status, body) = await PostAsync("void", order);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("voided", body.GetProperty("outcome").GetString());
    }

    /// <summary>Voiding captured money is refused; Orders reads it as a lost race.</summary>
    [Fact]
    public async Task Void_AfterCapturing_ShouldRefuseWithAlreadyCaptured()
    {
        var order = Guid.NewGuid();
        await AuthorizeAsync(order);
        await PostAsync("capture", order);

        var (status, body) = await PostAsync("void", order);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("already_captured", body.GetProperty("reason").GetString());
        Assert.NotEqual(Guid.Empty, body.GetProperty("paymentId").GetGuid());
    }

    [Fact]
    public async Task Void_WithNothingHeld_ShouldRefuseWithNoAuthorization()
    {
        var (status, body) = await PostAsync("void", Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("no_authorization", body.GetProperty("reason").GetString());
    }

    /// <summary>Authorising an order already paid is an answer, not a second charge.</summary>
    [Fact]
    public async Task Authorize_AfterCapturing_ShouldAnswerAlreadyCaptured()
    {
        var order = Guid.NewGuid();
        await AuthorizeAsync(order);
        await PostAsync("capture", order);

        var (status, body) = await AuthorizeAsync(order);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("already_captured", body.GetProperty("outcome").GetString());
    }

    /// <summary>Every refusal carries a <c>reason</c> in problem+json.</summary>
    [Fact]
    public async Task ARefusal_ShouldBeProblemJsonCarryingAReason()
    {
        using var response = await _client.PostAsJsonAsync(
            "capture", new { orderId = Guid.NewGuid(), clientId = Guid.NewGuid() });

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("reason").GetString()));
    }

    // -- helpers ----------------------------------------------------------

    private Task<(HttpStatusCode Status, JsonElement Body)> AuthorizeAsync(Guid orderId) =>
        SendAsync("authorize", new { orderId, clientId = ClientFor(orderId), amount = 42.00m, currency = "GBP" });

    private Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string route, Guid orderId) =>
        SendAsync(route, new { orderId, clientId = ClientFor(orderId) });

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(string route, object body)
    {
        using var response = await _client.PostAsJsonAsync(route, body);
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    /// <summary>The client derived from the order, so repeat calls present the same owner.</summary>
    private static Guid ClientFor(Guid orderId) => orderId;
}
