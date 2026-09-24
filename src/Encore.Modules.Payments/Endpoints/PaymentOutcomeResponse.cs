namespace Encore.Modules.Payments.Endpoints;

public sealed record PaymentOutcomeResponse(string Outcome, Guid PaymentId);
