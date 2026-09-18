namespace Encore.Modules.Payments.Simulation;

/// <summary>
/// Knobs for <see cref="SimulatedPaymentGateway"/>: how often it should
/// succeed, fail or hang, and how long it should take. Bound from
/// configuration so a load test can make the gateway as nasty as it likes
/// without a code change.
/// </summary>
public sealed class PaymentSimulationOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Payments:Simulation";

    // TODO: SuccessRate, FailureRate, TimeoutRate, MinLatency, MaxLatency, Seed.
}
