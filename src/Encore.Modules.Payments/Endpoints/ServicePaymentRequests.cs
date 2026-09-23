namespace Encore.Modules.Payments.Endpoints;

/// <summary>
/// What Orders sends to open or reuse an authorisation. The client id is in the body: here
/// it is a fact about the order, not the caller's identity.
/// </summary>
/// <param name="OrderId">The order being paid for.</param>
/// <param name="ClientId">Who the order belongs to.</param>
/// <param name="Amount">What is owed.</param>
/// <param name="Currency">ISO 4217 code for <paramref name="Amount"/>.</param>
public sealed record AuthorizeAttemptRequest(
    Guid OrderId,
    Guid ClientId,
    decimal Amount,
    string Currency);

/// <summary>What Orders sends to capture or void an order's live attempt. Keyed by order.</summary>
/// <param name="OrderId">The order whose live attempt is being settled.</param>
/// <param name="ClientId">Who the order belongs to.</param>
public sealed record OrderAttemptRequest(Guid OrderId, Guid ClientId);
