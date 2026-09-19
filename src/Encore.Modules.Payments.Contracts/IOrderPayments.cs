namespace Encore.Modules.Payments.Contracts;

/// <summary>
/// Taking money for an order, in the two phases that let a failed sale cost the
/// customer nothing: hold the funds, then either take them or let them go.
/// </summary>
/// <remarks>
/// <para>
/// Named for the need rather than the owner, as <c>IEventPricing</c> is and for
/// the same reason (019). An <c>IPaymentService</c> would invite every future
/// question about money — refunds, payouts, reconciliation, statements — to land
/// on this interface; <see cref="IOrderPayments"/> can only grow in one
/// direction, and the day it needs to grow in another the new need gets its own
/// contract.
/// </para>
/// <para>
/// <b>Keyed on the order, not on a payment id.</b> An order has at most one live
/// attempt against it at a time — a rule Postgres enforces, not this interface —
/// so the order id is enough to name the thing being captured or voided, and
/// Orders never has to store an id belonging to another module. Every request
/// also carries the client id, checked and never trusted, for the reason 011
/// gives for seat commands carrying an event id.
/// </para>
/// <para>
/// <b>No refund here.</b> A refund is a real operation this system does not have,
/// and an interface method for it would advertise a capability nothing can
/// deliver. <see cref="VoidAsync"/> is not a refund: it releases funds that were
/// never taken.
/// </para>
/// </remarks>
public interface IOrderPayments
{
    /// <summary>
    /// Asks the gateway to hold the order's total, so the seats can be sold
    /// knowing the money is there.
    /// </summary>
    /// <remarks>
    /// Idempotent against a live authorisation for the same order: a retry after a
    /// dropped response gets the existing hold back rather than a second one, the
    /// same way re-holding a seat you already hold is a no-op.
    /// </remarks>
    Task<AuthorizePaymentResponse> AuthorizeAsync(
        AuthorizePaymentRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes the money that was held. Called only once the seats are actually
    /// sold.
    /// </summary>
    Task<CapturePaymentResponse> CaptureAsync(
        CapturePaymentRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a hold without taking anything, because the order is not going to
    /// happen.
    /// </summary>
    Task<VoidPaymentResponse> VoidAsync(
        VoidPaymentRequest request,
        CancellationToken cancellationToken = default);
}
