namespace Encore.Modules.Payments.Contracts;

/// <summary>
/// Asks for the order's total to be held at the gateway.
/// </summary>
/// <remarks>The amount is Orders' snapshot of the total; Payments never re-derives it.</remarks>
/// <param name="OrderId">The order being paid for.</param>
/// <param name="ClientId">Who is paying, as claimed by <c>X-Client-Id</c>.</param>
/// <param name="Amount">The order total. Positive.</param>
/// <param name="Currency">ISO 4217 code for <paramref name="Amount"/>.</param>
public sealed record AuthorizePaymentRequest(
    Guid OrderId,
    Guid ClientId,
    decimal Amount,
    string Currency);
