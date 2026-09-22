namespace Encore.Modules.Payments.Simulation;

/// <summary>
/// What the gateway says it knows about an idempotency key when asked after the
/// fact. Separate from <see cref="GatewayOutcome"/> because a lookup answers a
/// different question: not "what did this call do" but "what, if anything, is on
/// record".
/// </summary>
/// <remarks>
/// The distinction that matters is <see cref="NotFound"/> against
/// <see cref="Unknown"/>. The first is an answer — the gateway looked and there is
/// nothing there, so the original request never arrived. The second is the absence
/// of one, and leaves the attempt exactly as ambiguous as it was. Collapsing them
/// would let a lookup that itself failed be read as proof that nothing happened,
/// which is the worst possible reading: it would void an order's slot while the
/// customer's funds were still held.
/// </remarks>
internal enum GatewayRecord
{
    /// <summary>Funds are held under this key, and <c>Reference</c> names them.</summary>
    Authorized = 0,

    /// <summary>The gateway received the call and refused it.</summary>
    Declined = 1,

    /// <summary>
    /// The gateway has no record of this key, so the original request never
    /// reached it and nothing was ever held.
    /// </summary>
    NotFound = 2,

    /// <summary>
    /// The lookup itself got no answer. Nothing has been learned and the attempt
    /// stays unresolved.
    /// </summary>
    Unknown = 3
}
