namespace Encore.Modules.Payments.Simulation;

public sealed class PaymentSimulationOptions
{
    public const string SectionName = "Payments:Simulation";

    public double DeclineRate { get; set; }

    public double TimeoutRate { get; set; }

    /// <summary>
    /// Of answered captures, the share refused, as for an authorisation that lapsed or was
    /// reversed: nothing is held afterwards (034).
    /// </summary>
    public double CaptureDeclineRate { get; set; }

    /// <summary>
    /// Of unanswered authorisations, the share whose request never arrived; the rest arrived and
    /// lost only the answer. Keeps both reconciliation branches reachable (014).
    /// </summary>
    public double LostRequestRate { get; set; } = 0.5;

    public TimeSpan MinLatency { get; set; } = TimeSpan.FromMilliseconds(50);

    public TimeSpan MaxLatency { get; set; } = TimeSpan.FromMilliseconds(250);

    public int? Seed { get; set; }
}
