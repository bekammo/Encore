using Encore.Modules.Inventory.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Encore.Modules.Inventory.UnitTests;

/// <summary>
/// The seat actions sell without an order or a payment, so they exist only when asked for, and
/// seat maps need the operator key (030). Mapping only: no request reaches a database.
/// </summary>
public sealed class SeatRoutesTests
{
    [Fact]
    public async Task SeatActions_ShouldBeOffByDefault()
    {
        await using var app = Map(exposeSeatRoutes: null);

        var routes = Routes(app);

        Assert.Contains(routes, route => route.EndsWith("/seats", StringComparison.Ordinal));
        Assert.DoesNotContain(routes, route => route.EndsWith("/hold", StringComparison.Ordinal));
        Assert.DoesNotContain(routes, route => route.EndsWith("/release", StringComparison.Ordinal));
        Assert.DoesNotContain(routes, route => route.EndsWith("/purchase", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SeatActions_WhenExposed_ShouldBeMappedWithTheHoldRateLimited()
    {
        await using var app = Map(exposeSeatRoutes: "true");

        var endpoints = Endpoints(app).ToList();

        var hold = Assert.Single(endpoints, endpoint => endpoint.RoutePattern.RawText!.EndsWith("/hold", StringComparison.Ordinal));
        Assert.Single(endpoints, endpoint => endpoint.RoutePattern.RawText!.EndsWith("/release", StringComparison.Ordinal));
        Assert.Single(endpoints, endpoint => endpoint.RoutePattern.RawText!.EndsWith("/purchase", StringComparison.Ordinal));

        Assert.Equal(
            SeatEndpoints.HoldRateLimitPolicy,
            hold.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName);
    }

    [Fact]
    public async Task MapInventoryModule_WithoutAnOperatorKey_ShouldFailAtStartup()
    {
        await using var app = Build(operatorKey: null, exposeSeatRoutes: null);

        var thrown = Assert.Throws<InvalidOperationException>(() => app.MapInventoryModule());
        Assert.Contains("Operator:ApiKey", thrown.Message, StringComparison.Ordinal);
    }

    private static WebApplication Map(string? exposeSeatRoutes)
    {
        var app = Build(operatorKey: "an-operator-key", exposeSeatRoutes);
        app.MapInventoryModule();
        return app;
    }

    private static WebApplication Build(string? operatorKey, string? exposeSeatRoutes)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Inventory"] = "Host=nowhere;Database=encore;Username=encore;Password=encore",
            ["ConnectionStrings:Redis"] = "localhost:1",
            ["Operator:ApiKey"] = operatorKey,
            [SeatEndpoints.ExposeSeatRoutesKey] = exposeSeatRoutes
        });
        builder.Services.AddInventoryModule(builder.Configuration);

        return builder.Build();
    }

    private static IEnumerable<RouteEndpoint> Endpoints(IEndpointRouteBuilder app) =>
        app.DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>();

    private static List<string> Routes(IEndpointRouteBuilder app) =>
        [.. Endpoints(app).Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)];
}
