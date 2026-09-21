using Encore.Modules.Payments;

var builder = WebApplication.CreateBuilder(args);

// The same three pieces of framework middleware Encore.Api registers, and for the
// same reason (049): without them a caller gets problem+json from the module and an
// empty body from the framework, and an unhandled exception returns nothing anybody
// can read. A service whose caller is another service needs this more, not less —
// the thing reading these responses is a switch statement.
builder.Services.AddProblemDetails();

// One module, composed through the same seam the monolith uses. This line is the
// whole of what makes extraction cheap: Payments does not know it has been moved,
// and nothing in it changed to allow this.
builder.Services.AddPaymentsModule(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// The customer-facing read routes. Whether this host should serve them at all is a
// deployment question rather than a design one — an ingress may well route
// GET /payments to the monolith and only /internal here — but a service that can
// answer "what happened to this payment" is more useful than one that cannot, and
// they are the same two read-only routes 033 settled.
app.MapPaymentsModule();

// The write side, and the reason this host exists. Throws at startup if
// Payments:ServiceToken is unset: an open authorise endpoint is not a thing to
// discover in production, so the service refuses to start instead.
app.MapPaymentsServiceApi(builder.Configuration);

app.Run();
