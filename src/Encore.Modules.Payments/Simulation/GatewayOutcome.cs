namespace Encore.Modules.Payments.Simulation;

/// <summary>What a gateway call did, in the gateway's own vocabulary.</summary>
internal enum GatewayOutcome
{
    Succeeded = 0,

    /// <summary>
    /// The gateway refused. Only authorisations are ever refused.
    /// </summary>
    Declined = 1,

    /// <summary>
    /// No answer came back; whether the gateway acted is unknown.
    /// </summary>
    TimedOut = 2
}
