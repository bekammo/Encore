using System.Diagnostics;
using OpenTelemetry.Trace;

namespace Encore.Telemetry;

/// <summary>
/// The root of a <see cref="ParentBasedSampler"/> (021), so it judges only spans nothing
/// started. An unparented client span is a background poll's query (outbox claim, expiry
/// sweep, reconciler): a one-span trace about once a second per job, burying the requests.
/// Npgsql's metrics still price them.
/// </summary>
internal sealed class DropUnparentedClientSpans : Sampler
{
    private static readonly SamplingResult Drop = new(SamplingDecision.Drop);
    private static readonly SamplingResult Keep = new(SamplingDecision.RecordAndSample);

    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters) =>
        samplingParameters.Kind is ActivityKind.Client ? Drop : Keep;
}
