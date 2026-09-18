using Encore.Modules.Catalog;
using Encore.Modules.Inventory;
using Encore.Modules.Orders;
using Encore.Modules.Payments;

var builder = WebApplication.CreateBuilder(args);

// One registration call per module, and the host is not allowed to know
// anything else about them. The method bodies are empty today; the seam
// existing today is the point. When a module is extracted into its own
// service later, this is the line that gets deleted — nothing else.
builder.Services
    .AddCatalogModule(builder.Configuration)
    .AddOrdersModule(builder.Configuration)
    .AddPaymentsModule(builder.Configuration)
    .AddInventoryModule(builder.Configuration);

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Inventory deliberately has no Map*Module() call: it exposes use cases, not
// CRUD, and its HTTP surface gets designed alongside the hold/sell flow.
app.MapCatalogModule();
app.MapOrdersModule();
app.MapPaymentsModule();

app.Run();
