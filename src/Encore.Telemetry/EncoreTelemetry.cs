using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Encore.Telemetry;

/// <summary>
/// Traces, metrics and logs over OTLP, for a host. Off unless an OTLP endpoint is configured,
/// so a run that did not ask for telemetry measures a system without it.
/// </summary>
public static class EncoreTelemetry
{
    /// <summary>The standard OTLP variable; its presence is the switch.</summary>
    public const string EndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    private const string MetricExportIntervalKey = "OTEL_METRIC_EXPORT_INTERVAL";

    /// <summary>
    /// Every module's source and meter. A wildcard, so this project never names a module.
    /// </summary>
    private const string EncoreInstruments = "Encore.*";

    /// <summary>Npgsql emits its own source and meter; no instrumentation package needed.</summary>
    private const string Npgsql = "Npgsql";

    /// <summary>The runtime's built-in meter: GC, thread pool, exceptions.</summary>
    private const string Runtime = "System.Runtime";

    public static IHostApplicationBuilder AddEncoreTelemetry(
        this IHostApplicationBuilder builder,
        string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        if (string.IsNullOrWhiteSpace(builder.Configuration[EndpointKey]))
        {
            return builder;
        }

        // A flash sale lasts a minute; the SDK's 60 s default would give a dashboard one point.
        // The standard variable still wins when set.
        if (builder.Configuration[MetricExportIntervalKey] is null)
        {
            builder.Services.Configure<MetricReaderOptions>(options =>
                options.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 5_000);
        }

        builder.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                // Parent-based: a span under a kept parent is kept; a root is judged alone.
                .SetSampler(new ParentBasedSampler(new DropUnparentedClientSpans()))
                // Probes would outnumber requests; they say nothing a trace can explain.
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddSource(Npgsql)
                .AddSource(EncoreInstruments))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter(Npgsql, Runtime, EncoreInstruments))
            .WithLogging(configureBuilder: null, configureOptions: options =>
            {
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;
            })
            .UseOtlpExporter();

        return builder;
    }
}
