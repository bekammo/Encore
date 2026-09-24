namespace Encore.Modules.Payments.Contracts;

public sealed record VoidPaymentRequest(Guid OrderId, Guid ClientId);
