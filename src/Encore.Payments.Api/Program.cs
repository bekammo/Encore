using Encore.Modules.Payments;
using Encore.Shared;
using Encore.Telemetry;

var builder = WebApplication.CreateBuilder(args);

// As in Encore.Api: off unless OTEL_EXPORTER_OTLP_ENDPOINT is set.
builder.AddEncoreTelemetry("encore-payments");

// Problem details for every response, as in Encore.Api.
builder.Services.AddProblemDetails();

// One module, composed through the same seam the monolith uses.
builder.Services.AddPaymentsModule(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Liveness only.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Readiness, as in Encore.Api. Copied because hosts may not reference each other.
app.MapGet("/health/ready", async (
    IEnumerable<IReadinessCheck> checks,
    CancellationToken cancellationToken) =>
{
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

    return ready
        ? Results.Ok(body)
        : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
});

// The customer-facing read-only routes.
app.MapPaymentsModule();

// The service API Orders calls. Throws at startup if Payments:ServiceToken is unset.
app.MapPaymentsServiceApi(builder.Configuration);

app.Run();
