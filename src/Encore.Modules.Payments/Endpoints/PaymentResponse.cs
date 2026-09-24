using Encore.Modules.Payments.Models;

namespace Encore.Modules.Payments.Endpoints;

public sealed record PaymentResponse(
    Guid Id,
    Guid OrderId,
    string Status,
    decimal Amount,
    string Currency,
    string? GatewayReference,
    DateTime AttemptedAt,
    DateTime? ResolvedAt)
{
    public static PaymentResponse From(Payment payment) =>
        new(
            payment.Id,
            payment.OrderId,
            SnakeCase(payment.Status.ToString()),
            payment.Amount,
            payment.Currency,
            payment.GatewayReference,
            payment.AttemptedAt,
            payment.ResolvedAt);

    private static string SnakeCase(string name) =>
        string.Concat(name.Select((character, index) =>
            char.IsUpper(character) && index > 0
                ? "_" + char.ToLowerInvariant(character)
                : char.ToLowerInvariant(character).ToString()));
}
