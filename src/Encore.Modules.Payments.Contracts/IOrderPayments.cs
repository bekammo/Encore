namespace Encore.Modules.Payments.Contracts;

/// <summary>
/// Two-phase, so a failed sale costs the customer nothing: hold the funds, then take them or let
/// them go. No refund. Keyed on the order, which has at most one live attempt; Orders never
/// stores a Payments id.
/// </summary>
public interface IOrderPayments
{
    Task<AuthorizePaymentResponse> AuthorizeAsync(
        AuthorizePaymentRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Called only once the seats are sold (010).</summary>
    Task<CapturePaymentResponse> CaptureAsync(
        CapturePaymentRequest request,
        CancellationToken cancellationToken = default);

    Task<VoidPaymentResponse> VoidAsync(
        VoidPaymentRequest request,
        CancellationToken cancellationToken = default);
}
