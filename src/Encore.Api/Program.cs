using Encore.Modules.Catalog;
using Encore.Modules.Inventory;
using Encore.Modules.Notifications;
using Encore.Modules.Orders;
using Encore.Modules.Payments;
using Encore.Shared;

var builder = WebApplication.CreateBuilder(args);

// Problem details for every response: module refusals, unhandled exceptions and
// framework statuses alike. This registration is the body factory the other two use.
builder.Services.AddProblemDetails();

// One call per module; the host knows nothing else about them. Notifications has no
// Map call: it serves no routes and is reached only by Inventory's outbox dispatcher.
builder.Services
    .AddCatalogModule(builder.Configuration)
    .AddOrdersModule(builder.Configuration)
    .AddPaymentsModule(builder.Configuration)
    .AddNotificationsModule(builder.Configuration)
    .AddInventoryModule(builder.Configuration);

var app = builder.Build();

// An unhandled exception becomes a 500 with a problem+json body.
app.UseExceptionHandler();

// Framework statuses (404, 405, 415, unbindable 400) get a problem+json body too.
app.UseStatusCodePages();

// Serves Swagger UI over the hand-written OpenAPI document at /docs/, from the same
// origin so "Try it out" works without CORS.
app.UseDefaultFiles();
app.UseStaticFiles();

// Liveness only.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Readiness: each module registers an IReadinessCheck and the host counts votes,
// because the host may not know that a module has a database.
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

    // 503: the host works, but something it depends on does not.
    return ready
        ? Results.Ok(body)
        : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapCatalogModule();
app.MapOrdersModule();
app.MapPaymentsModule();
app.MapInventoryModule();

app.Run();
