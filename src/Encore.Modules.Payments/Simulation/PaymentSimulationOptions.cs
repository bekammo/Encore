namespace Encore.Modules.Payments.Simulation;

/// <summary>
/// Knobs for <see cref="SimulatedPaymentGateway"/>: how often it should decline or
/// hang, and how long it should take. Bound from configuration so a load test can
/// make the gateway as nasty as it likes without a code change.
/// </summary>
/// <remarks>
/// <b>Two rates, not three.</b> The stub's note asked for success, failure and
/// timeout rates, which is a trio that has to sum to one and therefore a
/// validation rule and an error message for when it does not. Success is the
/// remainder instead: it cannot be set wrong, and nothing has to check it.
/// </remarks>
public sealed class PaymentSimulationOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Payments:Simulation";

    /// <summary>
    /// Probability that an authorisation is refused, in <c>[0, 1]</c>. Applies to
    /// authorisation only — a capture or a void is never declined here, which is
    /// the simplification <c>DECISIONS.md</c> 032 records.
    /// </summary>
    public double DeclineRate { get; set; }

    /// <summary>
    /// Probability that any call gets no answer, in <c>[0, 1]</c>. This is the
    /// interesting knob: a timeout is the one outcome that leaves the system
    /// genuinely unsure what happened at the other end.
    /// </summary>
    public double TimeoutRate { get; set; }

    /// <summary>
    /// Given that a call got no answer, the probability the request never reached
    /// the gateway at all, in <c>[0, 1]</c>. Half by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A different axis from the two above, which is why it does not reopen the
    /// sum-to-one problem the remarks on this class refuse.</b> Those two divide
    /// every call into answered-yes, answered-no and unanswered. This one divides
    /// the unanswered ones into the two things "unanswered" can physically mean:
    /// the request was lost on the way there, so nothing happened, or it arrived
    /// and the answer was lost coming back, so something did.
    /// </para>
    /// <para>
    /// <b>It exists because reconciliation has two branches and both have to be
    /// reachable.</b> At <c>0</c> every timed-out authorisation turns out to have
    /// landed; at <c>1</c> none of them did. A simulator that only ever produced
    /// one of those would leave the other path untested while looking thoroughly
    /// exercised.
    /// </para>
    /// </remarks>
    public double LostRequestRate { get; set; } = 0.5;

    /// <summary>Shortest the gateway takes to answer.</summary>
    public TimeSpan MinLatency { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Longest the gateway takes to answer. Clamped up to
    /// <see cref="MinLatency"/> if it is set lower, so a mistyped pair slows the
    /// gateway down rather than throwing at startup.
    /// </summary>
    public TimeSpan MaxLatency { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Fixes the random sequence, so a run is reproducible. Null — the default —
    /// means a different sequence every time, which is what a load test wants and
    /// what a test asserting on outcomes must never have.
    /// </summary>
    public int? Seed { get; set; }
}
