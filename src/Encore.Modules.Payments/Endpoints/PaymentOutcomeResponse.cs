namespace Encore.Modules.Payments.Endpoints;

/// <summary>
/// The body of a successful service call. Refusals are problem+json with a <c>reason</c>;
/// callers branch on <c>outcome</c> and <c>reason</c>, never on the status code.
/// </summary>
/// <param name="Outcome">The status, snake_cased, as the contract enum names it.</param>
/// <param name="PaymentId">The attempt this is about.</param>
public sealed record PaymentOutcomeResponse(string Outcome, Guid PaymentId);
