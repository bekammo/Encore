namespace Encore.Modules.Payments.Endpoints;

/// <summary>
/// What Orders sends to open or reuse an authorisation. DECISIONS 061.
/// </summary>
/// <remarks>
/// The client id is in the body rather than in a header, and that is the whole
/// difference between this and the read routes. On those, <c>X-Client-Id</c> is the
/// caller <i>asserting who it is</i>. Here the caller is Orders, and the client id
/// is a fact about the order being paid for that Orders is reporting. Putting it in
/// a header would make the two look like the same kind of claim.
/// </remarks>
/// <param name="OrderId">The order being paid for.</param>
/// <param name="ClientId">Who the order belongs to.</param>
/// <param name="Amount">What is owed.</param>
/// <param name="Currency">ISO 4217 code for <paramref name="Amount"/>.</param>
public sealed record AuthorizeAttemptRequest(
    Guid OrderId,
    Guid ClientId,
    decimal Amount,
    string Currency);

/// <summary>
/// What Orders sends to capture or void an order's live attempt. DECISIONS 061.
/// </summary>
/// <remarks>
/// Keyed by order rather than by payment, because <c>IOrderPayments</c> is: the
/// caller knows which order it is confirming and the module owns which attempt that
/// currently means. Re-keying on a payment id would make Orders store one, and the
/// one-live-attempt index already answers the question that id would be asked.
/// </remarks>
/// <param name="OrderId">The order whose live attempt is being settled.</param>
/// <param name="ClientId">Who the order belongs to.</param>
public sealed record OrderAttemptRequest(Guid OrderId, Guid ClientId);
