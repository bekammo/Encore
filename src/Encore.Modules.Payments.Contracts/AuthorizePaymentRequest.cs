namespace Encore.Modules.Payments.Contracts;

/// <summary>
/// Asks for the order's total to be held at the gateway.
/// </summary>
/// <remarks>
/// The amount and currency are passed in rather than looked up, because Payments
/// has no idea what an order costs and should not learn: the total is Orders'
/// snapshot of what the customer agreed to pay, taken at checkout from Catalog's
/// price, and re-deriving it here would be a second copy of a number that must not
/// drift.
/// </remarks>
/// <param name="OrderId">The order being paid for.</param>
/// <param name="ClientId">Who is paying, as claimed by <c>X-Client-Id</c>.</param>
/// <param name="Amount">The order total. Positive.</param>
/// <param name="Currency">ISO 4217 code for <paramref name="Amount"/>.</param>
public sealed record AuthorizePaymentRequest(
    Guid OrderId,
    Guid ClientId,
    decimal Amount,
    string Currency);
