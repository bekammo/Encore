namespace Encore.Modules.Payments.Contracts;

/// <summary>
/// Asks for the funds held against an order to be released without being taken.
/// </summary>
/// <param name="OrderId">The order whose authorisation should be released.</param>
/// <param name="ClientId">Who is paying, as claimed by <c>X-Client-Id</c>.</param>
public sealed record VoidPaymentRequest(Guid OrderId, Guid ClientId);
