namespace Encore.Modules.Payments.Simulation;

/// <summary>
/// What a call to the gateway did. The gateway's own vocabulary, deliberately
/// smaller than the module's: it says what happened on the wire, and
/// <c>InProcessOrderPayments</c> decides what that means for a payment.
/// </summary>
internal enum GatewayOutcome
{
    /// <summary>The gateway did what was asked.</summary>
    Succeeded = 0,

    /// <summary>
    /// The gateway refused. Only an authorisation can be refused — see
    /// <c>PaymentSimulationOptions.DeclineRate</c>.
    /// </summary>
    Declined = 1,

    /// <summary>
    /// No answer came back. Whether the other end acted is unknown, and that
    /// ambiguity is the point of this outcome existing.
    /// </summary>
    TimedOut = 2
}
