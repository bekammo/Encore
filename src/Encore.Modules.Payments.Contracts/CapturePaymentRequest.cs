namespace Encore.Modules.Payments.Contracts;

/// <summary>
/// Asks for the funds held against an order to be taken.
/// </summary>
/// <param name="OrderId">The order whose authorisation should be captured.</param>
/// <param name="ClientId">Who is paying, as claimed by <c>X-Client-Id</c>.</param>
public sealed record CapturePaymentRequest(Guid OrderId, Guid ClientId);
