using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Encore.Modules.Catalog.IntegrationTests;

public sealed class CatalogEndpointsTests(CatalogDatabase database)
    : IClassFixture<CatalogDatabase>, IAsyncLifetime
{
    private const string OperatorKeyValue = "catalog-tests-operator-key";

    private readonly CatalogDatabase _database = database;

    private WebApplication _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Catalog"] = _database.ConnectionString,
            ["Operator:ApiKey"] = OperatorKeyValue
        });

        builder.Services.AddProblemDetails();
        builder.Services.AddCatalogModule(builder.Configuration);

        _host = builder.Build();
        _host.UseExceptionHandler();
        _host.UseStatusCodePages();
        _host.MapCatalogModule();

        await _host.StartAsync();

        var address = _host.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .Single();

        _client = new HttpClient { BaseAddress = new Uri(address.TrimEnd('/') + "/catalog/") };
        _client.DefaultRequestHeaders.Add("X-Operator-Key", OperatorKeyValue);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
    }

    // Payments refuses to authorise nothing, so a free event could be listed but never bought (028).
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task CreateEvent_WithoutAPositivePrice_ShouldRefuse(string price)
    {
        using var response = await PostEventAsync(await CreateVenueAsync(), decimal.Parse(price));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_price", problem.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task CreateEvent_WithTheSmallestPrice_ShouldBeCreated()
    {
        using var response = await PostEventAsync(await CreateVenueAsync(), 0.0001m);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // Operator writes are refused without the key; reads stay public (030).
    [Theory]
    [InlineData(null)]
    [InlineData("not-the-operator-key")]
    public async Task CreateVenue_WithoutTheOperatorKey_ShouldRefuse(string? key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "venues")
        {
            Content = JsonContent.Create(new { name = "Hall", address = "1 Street", capacity = 100 })
        };

        if (key is not null)
        {
            request.Headers.Add("X-Operator-Key", key);
        }

        // A client without the fixture's default key.
        using var anonymous = new HttpClient { BaseAddress = _client.BaseAddress };
        using var response = await anonymous.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("operator_key_invalid", problem.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ListVenues_WithoutTheOperatorKey_ShouldAnswer()
    {
        using var anonymous = new HttpClient { BaseAddress = _client.BaseAddress };
        using var response = await anonymous.GetAsync("venues");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task MapCatalogModule_WithoutAnOperatorKey_ShouldFailAtStartup()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Catalog"] = _database.ConnectionString
        });
        builder.Services.AddCatalogModule(builder.Configuration);

        await using var host = builder.Build();

        var thrown = Assert.Throws<InvalidOperationException>(() => host.MapCatalogModule());
        Assert.Contains("Operator:ApiKey", thrown.Message, StringComparison.Ordinal);
    }

    private async Task<Guid> CreateVenueAsync()
    {
        using var response = await _client.PostAsJsonAsync(
            "venues", new { name = "Hall", address = "1 Street", capacity = 100 });

        response.EnsureSuccessStatusCode();

        var venue = await response.Content.ReadFromJsonAsync<JsonElement>();
        return venue.GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> PostEventAsync(Guid venueId, decimal price) =>
        _client.PostAsJsonAsync("events", new
        {
            venueId,
            name = "Show",
            startsAt = "2027-07-01T19:30:00Z",
            price,
            currency = "GBP"
        });
}
