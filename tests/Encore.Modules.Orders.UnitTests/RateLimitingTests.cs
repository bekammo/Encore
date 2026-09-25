using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Encore.Modules.Orders.Endpoints;
using Encore.Modules.Shared.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Encore.Modules.Orders.UnitTests;

/// <summary>The per-IP floor under checkout (030): the policy's behaviour, and that checkout carries it.</summary>
public sealed class RateLimitingTests
{
    private const string Probe = "probe";

    [Fact]
    public async Task APolicy_WhenTheBucketIsEmpty_ShouldRefuseWithAReasonAndRetryAfter()
    {
        await using var host = await ProbeHostAsync(enabled: true, burst: 2);

        var statuses = new List<HttpResponseMessage>();

        try
        {
            for (var i = 0; i < 10; i++)
            {
                statuses.Add(await host.Client.PostAsync("/probe", content: null));
            }

            // The bucket starts full, so the burst always gets through.
            Assert.Equal(HttpStatusCode.OK, statuses[0].StatusCode);
            Assert.Equal(HttpStatusCode.OK, statuses[1].StatusCode);

            var refused = statuses.FirstOrDefault(response => response.StatusCode == HttpStatusCode.TooManyRequests);
            Assert.NotNull(refused);
            Assert.True(refused.Headers.Contains("Retry-After"), "A refusal should say when to come back.");

            var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("rate_limited", problem.GetProperty("reason").GetString());
            Assert.True(problem.GetProperty("retriable").GetBoolean());
        }
        finally
        {
            statuses.ForEach(response => response.Dispose());
        }
    }

    [Fact]
    public async Task APolicy_WhenDisabled_ShouldNeverRefuse()
    {
        await using var host = await ProbeHostAsync(enabled: false, burst: 1);

        for (var i = 0; i < 30; i++)
        {
            using var response = await host.Client.PostAsync("/probe", content: null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public void AddPerIpRateLimitPolicy_WithANonPositiveRate_ShouldRefuseToRegister()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{PerIpRateLimiting.SectionName}:PermitsPerSecond"] = "0"
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddPerIpRateLimitPolicy(configuration, Probe));
    }

    [Fact]
    public async Task Checkout_ShouldCarryItsRateLimitPolicy()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Orders"] = "Host=nowhere;Database=encore;Username=encore;Password=encore"
        });
        builder.Services.AddOrdersModule(builder.Configuration);

        await using var app = builder.Build();
        app.MapOrdersModule();

        var checkout = Endpoints(app).Single(endpoint =>
            endpoint.RoutePattern.RawText?.TrimEnd('/') == "/orders"
            && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("POST") == true);

        Assert.Equal(
            OrderEndpoints.CheckoutRateLimitPolicy,
            checkout.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName);
    }

    private static IEnumerable<RouteEndpoint> Endpoints(IEndpointRouteBuilder app) =>
        app.DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>();

    private static async Task<ProbeHost> ProbeHostAsync(bool enabled, int burst)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{PerIpRateLimiting.SectionName}:Enabled"] = enabled ? "true" : "false",
            [$"{PerIpRateLimiting.SectionName}:PermitsPerSecond"] = "1",
            [$"{PerIpRateLimiting.SectionName}:Burst"] = burst.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });

        builder.Services.AddPerIpRateLimitPolicy(builder.Configuration, Probe);

        var app = builder.Build();
        app.UseRateLimiter();
        app.MapPost("/probe", () => Results.Ok()).RequireRateLimiting(Probe);

        await app.StartAsync();

        var address = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .Single();

        return new ProbeHost(app, new HttpClient { BaseAddress = new Uri(address) });
    }

    private sealed class ProbeHost(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }
}
