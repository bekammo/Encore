using Encore.Modules.Payments;
using Encore.Shared;
using Encore.Telemetry;

var builder = WebApplication.CreateBuilder(args);

builder.AddEncoreTelemetry("encore-payments");

builder.Services.AddProblemDetails();

builder.Services.AddPaymentsModule(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// A copy of Encore.Api's readiness endpoint: hosts may not reference each other.
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

app.MapPaymentsModule();
app.MapPaymentsServiceApi(builder.Configuration);

app.Run();
