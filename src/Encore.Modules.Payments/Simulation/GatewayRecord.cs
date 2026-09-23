namespace Encore.Modules.Payments.Simulation;

/// <summary>
/// What the gateway has on record for a key, asked after the fact. <see cref="NotFound"/> is
/// an answer (nothing arrived); <see cref="Unknown"/> is no answer, and must never be read as
/// the first.
/// </summary>
internal enum GatewayRecord
{
    /// <summary>Funds are held under this key, and <c>Reference</c> names them.</summary>
    Authorized = 0,

    /// <summary>The gateway received the call and refused it.</summary>
    Declined = 1,

    /// <summary>
    /// No record: the request never arrived and nothing was held.
    /// </summary>
    NotFound = 2,

    /// <summary>
    /// The lookup itself got no answer. Nothing learned.
    /// </summary>
    Unknown = 3
}
