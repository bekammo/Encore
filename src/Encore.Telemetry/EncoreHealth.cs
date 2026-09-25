using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Encore.Telemetry;

/// <summary>
/// The framework's health checks behind the JSON the hand-written document promises. Modules
/// register the checks; both hosts map them here, so the two routes are written once (029).
/// </summary>
public static class EncoreHealth
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // MapHealthChecks answers every method; the document promises GET.
    private static readonly HttpMethodMetadata GetOrHead = new(["GET", "HEAD"]);

    public static IEndpointRouteBuilder MapEncoreHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Liveness runs no check: a dependency's outage must not get a working process restarted.
        endpoints.MapHealthChecks("/health", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteLivenessAsync
        }).WithMetadata(GetOrHead);

        // Unhealthy is 503, not 500: the host works but a dependency does not.
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            ResponseWriter = WriteReadinessAsync
        }).WithMetadata(GetOrHead);

        return endpoints;
    }

    private static Task WriteLivenessAsync(HttpContext context, HealthReport report) =>
        context.Response.WriteAsJsonAsync(new { status = "ok" }, Json);

    private static Task WriteReadinessAsync(HttpContext context, HealthReport report)
    {
        var body = new
        {
            status = report.Status is HealthStatus.Unhealthy ? "not_ready" : "ready",
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new
                {
                    ready = entry.Value.Status is not HealthStatus.Unhealthy,
                    detail = entry.Value.Description ?? entry.Value.Exception?.Message ?? string.Empty
                })
        };

        return context.Response.WriteAsJsonAsync(body, Json);
    }
}
