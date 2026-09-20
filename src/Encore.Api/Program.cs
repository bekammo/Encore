using Encore.Modules.Catalog;
using Encore.Modules.Inventory;
using Encore.Modules.Notifications;
using Encore.Modules.Orders;
using Encore.Modules.Payments;

var builder = WebApplication.CreateBuilder(args);

// Three pieces of middleware in the host, all framework rather than package, so
// Encore.Api keeps its zero-PackageReference property. Together they make every
// response one shape: without them a caller gets problem+json from the modules
// and an empty body from the framework, and an unhandled exception under load
// returns nothing anybody can read.
//
// This registration is the body factory the other two use. On its own it changes
// no response at all, which is the part 014 got wrong and 049 corrects.
builder.Services.AddProblemDetails();

// One registration call per module, and the host is not allowed to know
// anything else about them. Inventory's body registers a DbContext, a Redis
// multiplexer, its ports and its use cases; Catalog and Orders register a
// DbContext and a service apiece; Payments registers a DbContext, a gateway and
// its public face. The host cannot tell which is which, and that is the point.
// When a module is extracted into its own service later, this is the line that
// gets deleted — nothing else, and Payments is the one 004 nominated to go first.
//
// Notifications has no Map call below, and the asymmetry is deliberate rather
// than an omission. It serves no routes: its entire inbound surface is a handler
// that Inventory's outbox dispatcher resolves from this container. The host does
// not know that, any more than it knows the others have databases — it registers
// a module and the module says what it is.
builder.Services
    .AddCatalogModule(builder.Configuration)
    .AddOrdersModule(builder.Configuration)
    .AddPaymentsModule(builder.Configuration)
    .AddNotificationsModule(builder.Configuration)
    .AddInventoryModule(builder.Configuration);

var app = builder.Build();

// Covers the unhandled exception: a 500 with a problem+json body rather than a
// dropped connection.
app.UseExceptionHandler();

// Covers every status the framework produces before or instead of an endpoint —
// 404 for an unmatched route, 405 for the wrong verb, 415 for the wrong content
// type, 400 for a body that will not bind. All four were measured returning a
// zero-length body with no content-type until this line existed; AddProblemDetails
// alone does not reach them, because nothing was asking it for a body. 049.
//
// What it does not do is give those responses a `reason`. A framework refusal is
// about the request being malformed rather than about the state of the world, so
// there is no closed vocabulary to draw one from, and clients still branch on
// `reason` only for the module refusals that carry one.
app.UseStatusCodePages();

// Serves wwwroot/docs — Swagger UI over a hand-written OpenAPI document — at
// /docs/. Both are framework middleware, so ENCORE001 still holds and the host
// still has no package of its own: 014 refused Swashbuckle and
// Microsoft.AspNetCore.OpenApi, and this does not reintroduce either.
//
// It is served from the host rather than opened off disk so that Try it out
// works. The page and the API then share an origin, which is what lets the
// browser make the call — a file:// page would be a null origin and would need
// a CORS policy that does not exist and should not have to.
//
// The cost is real and is recorded in 049: the document is maintained by hand,
// so nothing fails when a route changes and the document does not.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapCatalogModule();
app.MapOrdersModule();
app.MapPaymentsModule();
app.MapInventoryModule();

app.Run();
