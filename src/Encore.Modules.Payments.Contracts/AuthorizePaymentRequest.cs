namespace Encore.Modules.Payments.Contracts;

/// <param name="Amount">Positive.</param>
public sealed record AuthorizePaymentRequest(
    Guid OrderId,
    Guid ClientId,
    decimal Amount,
    string Currency);
