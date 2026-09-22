namespace Encore.Modules.Payments.Endpoints;

/// <summary>
/// The body of a successful service call: what happened, and which attempt it
/// happened to. DECISIONS 061.
/// </summary>
/// <remarks>
/// <para>
/// Only the outcomes that are <i>answers</i> come back this way — <c>authorized</c>,
/// <c>already_captured</c>, <c>captured</c>, <c>voided</c>. Everything else is a
/// problem+json body carrying a <c>reason</c>, which is the shape 049 made every
/// refusal in this host take.
/// </para>
/// <para>
/// <b>The caller branches on <c>outcome</c> and <c>reason</c>, never on the status
/// code.</b> The codes are chosen so a proxy or a human reading a log sees something
/// sensible — 402 for a decline, 504 for a gateway that never answered, 409 for a
/// race — but several statuses share a code, and the string is the half that is
/// one-to-one with <c>IOrderPayments</c>' vocabulary.
/// </para>
/// </remarks>
/// <param name="Outcome">The status, snake_cased, as the contract enum names it.</param>
/// <param name="PaymentId">The attempt this is about.</param>
public sealed record PaymentOutcomeResponse(string Outcome, Guid PaymentId);
