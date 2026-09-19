using Encore.Modules.Catalog;
using Encore.Modules.Inventory;
using Encore.Modules.Orders;
using Encore.Modules.Payments;

var builder = WebApplication.CreateBuilder(args);

// The only two pieces of middleware in the host, and both are framework rather
// than package, so Encore.Api keeps its zero-PackageReference property. They
// earn their place by making every response one shape: without them a framework
// 404 or 415 comes back with an empty body while the modules return
// problem+json, and an unhandled exception under load returns nothing a caller
// can read.
builder.Services.AddProblemDetails();

// One registration call per module, and the host is not allowed to know
// anything else about them. Inventory's body registers a DbContext, a Redis
// multiplexer, its ports and its use cases; Catalog and Orders register a
// DbContext and a service apiece; Payments registers a DbContext, a gateway and
// its public face. The host cannot tell which is which, and that is the point.
// When a module is extracted into its own service later, this is the line that
// gets deleted — nothing else, and Payments is the one 004 nominated to go first.
builder.Services
    .AddCatalogModule(builder.Configuration)
    .AddOrdersModule(builder.Configuration)
    .AddPaymentsModule(builder.Configuration)
    .AddInventoryModule(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapCatalogModule();
app.MapOrdersModule();
app.MapPaymentsModule();
app.MapInventoryModule();

app.Run();
