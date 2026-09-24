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
            ["ConnectionStrings:Catalog"] = _database.ConnectionString
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
