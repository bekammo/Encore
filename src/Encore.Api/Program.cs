using Encore.Modules.Catalog;
using Encore.Modules.Inventory;
using Encore.Modules.Notifications;
using Encore.Modules.Orders;
using Encore.Modules.Payments;
using Encore.Shared;
using Encore.Telemetry;

var builder = WebApplication.CreateBuilder(args);

builder.AddEncoreTelemetry("encore-api");

builder.Services.AddProblemDetails();

builder.Services
    .AddCatalogModule(builder.Configuration)
    .AddOrdersModule(builder.Configuration)
    .AddPaymentsModule(builder.Configuration)
    .AddNotificationsModule(builder.Configuration)
    .AddInventoryModule(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Swagger UI at /docs/ over the hand-written openapi.json (008), same-origin so "Try it out"
// needs no CORS.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/health/ready", async (
    IEnumerable<IReadinessCheck> checks,
    CancellationToken cancellationToken) =>
{
    // Sequential: checks share a request scope, and two on one DbContext cannot run concurrently.
    var results = new Dictionary<string, ReadinessResult>();

    foreach (var check in checks)
    {
        results[check.Name] = await check.CheckAsync(cancellationToken);
    }

    var ready = results.Values.All(result => result.Ready);

    var body = new
    {
        status = ready ? "ready" : "not_ready",
        checks = results.ToDictionary(
            entry => entry.Key,
            entry => new { ready = entry.Value.Ready, detail = entry.Value.Detail })
    };

    // 503, not 500: the host works but a dependency does not.
    return ready
        ? Results.Ok(body)
        : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
});

// Notifications serves no routes, so it has no Map.
app.MapCatalogModule();
app.MapOrdersModule();
app.MapPaymentsModule();
app.MapInventoryModule();

app.Run();
