using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Encore.Telemetry;

/// <summary>
/// Off unless <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set, so a run that did not ask for
/// telemetry measures a system without it (021).
/// </summary>
public static class EncoreTelemetry
{
    private const string EndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    private const string MetricExportIntervalKey = "OTEL_METRIC_EXPORT_INTERVAL";

    // A wildcard, so this project never names a module (021).
    private const string EncoreInstruments = "Encore.*";

    // Npgsql and the runtime emit their own telemetry; no instrumentation package needed.
    private const string Npgsql = "Npgsql";
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
        if (builder.Configuration[MetricExportIntervalKey] is null)
        {
            builder.Services.Configure<MetricReaderOptions>(options =>
                options.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 5_000);
        }

        builder.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .SetSampler(new ParentBasedSampler(new DropUnparentedClientSpans()))
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
