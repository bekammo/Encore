namespace Encore.Modules.Payments.Contracts;

public sealed record CapturePaymentRequest(Guid OrderId, Guid ClientId);
