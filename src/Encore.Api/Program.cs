using Encore.Modules.Catalog;
using Encore.Modules.Inventory;
using Encore.Modules.Notifications;
using Encore.Modules.Orders;
using Encore.Modules.Payments;
using Encore.Telemetry;

var builder = WebApplication.CreateBuilder(args);

builder.AddEncoreTelemetry("encore-api");

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

builder.Services
    .AddCatalogModule(builder.Configuration)
    .AddOrdersModule(builder.Configuration)
    .AddPaymentsModule(builder.Configuration)
    .AddNotificationsModule(builder.Configuration)
    .AddInventoryModule(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Modules attach their policies to routes; without this middleware every policy is skipped (030).
app.UseRateLimiter();

// Swagger UI at /docs/ over the hand-written openapi.json (008), same-origin so "Try it out"
// needs no CORS.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapEncoreHealthChecks();

// Notifications serves no routes, so it has no Map.
app.MapCatalogModule();
app.MapOrdersModule();
app.MapPaymentsModule();
app.MapInventoryModule();

app.Run();
