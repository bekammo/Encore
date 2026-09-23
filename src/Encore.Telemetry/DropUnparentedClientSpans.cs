using System.Diagnostics;
using OpenTelemetry.Trace;

namespace Encore.Telemetry;

/// <summary>
/// The root sampler: drops a client span that nothing started, and keeps everything else.
/// </summary>
/// <remarks>
/// A client span with no parent is a background poll's query — the outbox claim, the expiry
/// sweep, the reconciler — and each arrives every second or so as a trace of one span, burying
/// the requests. Their cost still shows in Npgsql's metrics. A query under a request or an
/// outbox delivery has a parent, so the parent decides, and it is kept.
/// </remarks>
internal sealed class DropUnparentedClientSpans : Sampler
{
    private static readonly SamplingResult Drop = new(SamplingDecision.Drop);
    private static readonly SamplingResult Keep = new(SamplingDecision.RecordAndSample);

    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters) =>
        samplingParameters.Kind is ActivityKind.Client ? Drop : Keep;
}
