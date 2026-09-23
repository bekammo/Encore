namespace Encore.Modules.Payments.Simulation;

/// <summary>
/// Settings for <see cref="SimulatedPaymentGateway"/>: how often it declines or hangs and how
/// slow it is. Success is whatever is left, so the rates cannot be set inconsistently.
/// </summary>
public sealed class PaymentSimulationOptions
{
    public const string SectionName = "Payments:Simulation";

    /// <summary>
    /// Probability an authorisation is refused. Captures and voids are never declined.
    /// </summary>
    public double DeclineRate { get; set; }

    /// <summary>
    /// Probability any call gets no answer.
    /// </summary>
    public double TimeoutRate { get; set; }

    /// <summary>
    /// Of unanswered calls, the share whose request never arrived (the rest arrived and only
    /// the answer was lost). Keeps both reconciliation branches reachable.
    /// </summary>
    public double LostRequestRate { get; set; } = 0.5;

    public TimeSpan MinLatency { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Clamped up to <see cref="MinLatency"/> if set lower.
    /// </summary>
    public TimeSpan MaxLatency { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Fixes the random sequence for reproducible runs. Null means a different sequence each time.
    /// </summary>
    public int? Seed { get; set; }
}
