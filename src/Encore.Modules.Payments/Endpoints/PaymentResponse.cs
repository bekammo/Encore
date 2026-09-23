using Encore.Modules.Payments.Models;

namespace Encore.Modules.Payments.Endpoints;

/// <summary>An attempt to charge for an order, as a client sees it.</summary>
/// <param name="Id">Identity of the attempt.</param>
/// <param name="OrderId">The order being paid for.</param>
/// <param name="Status">Where the attempt has got to, as a lowercase string.</param>
/// <param name="Amount">What was asked for.</param>
/// <param name="Currency">ISO 4217 code for <paramref name="Amount"/>.</param>
/// <param name="GatewayReference">The gateway's handle on the hold, if there is one.</param>
/// <param name="AttemptedAt">When the current attempt was made, in UTC.</param>
/// <param name="ResolvedAt">When the attempt got its answer, an ending or a timeout, if it has.</param>
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
    /// <summary>Renders an attempt.</summary>
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

    /// <summary>
    /// <c>TimedOut</c> to <c>timed_out</c>.
    /// </summary>
    private static string SnakeCase(string name) =>
        string.Concat(name.Select((character, index) =>
            char.IsUpper(character) && index > 0
                ? "_" + char.ToLowerInvariant(character)
                : char.ToLowerInvariant(character).ToString()));
}
