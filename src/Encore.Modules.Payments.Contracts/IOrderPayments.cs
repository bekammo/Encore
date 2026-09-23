namespace Encore.Modules.Payments.Contracts;

/// <summary>
/// Taking money for an order, in the two phases that let a failed sale cost the
/// customer nothing: hold the funds, then either take them or let them go.
/// </summary>
/// <remarks>
/// Keyed on the order, since an order has at most one live attempt; Orders never stores a
/// Payments id. There is no refund: <see cref="VoidAsync"/> releases funds never taken.
/// </remarks>
public interface IOrderPayments
{
    /// <summary>
    /// Holds the order's total at the gateway. Idempotent against a live authorisation.
    /// </summary>
    Task<AuthorizePaymentResponse> AuthorizeAsync(
        AuthorizePaymentRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes the held money. Called only once the seats are sold.
    /// </summary>
    Task<CapturePaymentResponse> CaptureAsync(
        CapturePaymentRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a hold without taking anything.
    /// </summary>
    Task<VoidPaymentResponse> VoidAsync(
        VoidPaymentRequest request,
        CancellationToken cancellationToken = default);
}
