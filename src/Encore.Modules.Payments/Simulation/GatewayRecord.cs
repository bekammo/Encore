namespace Encore.Modules.Payments.Simulation;

/// <summary>
/// <see cref="NotFound"/> is an answer: the gateway looked and has nothing. <see cref="Unknown"/>
/// is no answer, and must never be read as the first (014).
/// </summary>
internal enum GatewayRecord
{
    Authorized = 0,
    Declined = 1,
    NotFound = 2,
    Unknown = 3
}
