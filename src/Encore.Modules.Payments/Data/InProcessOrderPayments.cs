using Encore.Modules.Payments.Contracts;
using Encore.Modules.Payments.Models;
using Encore.Modules.Payments.Simulation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Encore.Modules.Payments.Data;

/// <summary>
/// Serves <see cref="IOrderPayments"/> from this process, against this module's
/// own tables and its simulated gateway.
/// </summary>
/// <remarks>
/// <para>
/// <b>It lives in <c>Data/</c> rather than an <c>Adapters/</c> folder.</b> Catalog
/// set that precedent with <c>InProcessEventPricing</c> and the argument is the
/// same: Inventory's <c>Adapters/</c> exists because it has ports to adapt, and
/// borrowing the folder name here would advertise a hexagon that is not there.
/// </para>
/// <para>
/// <b>The row is written before the gateway is called, every time.</b> That
/// ordering is the whole recovery story. A crash between the two leaves a
/// <see cref="PaymentStatus.Pending"/> row holding this order's one live slot, and
/// the next attempt finds it and asks the gateway the same question under the same
/// idempotency key. Calling first and writing afterwards would leave nothing
/// behind, so the retry would invent a new key — and a new key against a gateway
/// that did receive the first call is a second authorisation. See
/// <c>DECISIONS.md</c> 031.
/// </para>
/// </remarks>
internal sealed class InProcessOrderPayments(
    PaymentsDbContext payments,
    SimulatedPaymentGateway gateway,
    TimeProvider clock) : IOrderPayments
{
    /// <summary>The unique index that is the real guard against a double charge.</summary>
    private const string LiveAttemptIndex = "ux_payments_order_live";

    private readonly PaymentsDbContext _payments = payments;
    private readonly SimulatedPaymentGateway _gateway = gateway;
    private readonly TimeProvider _clock = clock;

    /// <inheritdoc />
    public async Task<AuthorizePaymentResponse> AuthorizeAsync(
        AuthorizePaymentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payment = await LiveAsync(request.OrderId, request.ClientId, cancellationToken)
            .ConfigureAwait(false);

        var utcNow = _clock.GetUtcNow().UtcDateTime;

        switch (payment?.Status)
        {
            // Already paid. A confirm retried after a complete success must not
            // charge anybody twice, so this is an answer rather than a refusal.
            case PaymentStatus.Captured:
                return AuthorizePaymentResponse.AlreadyCaptured(payment.Id);

            // Funds are already held for this order. Idempotent, for the reason
            // re-holding a seat you already hold is (007): a retry after a dropped
            // response must get back what it already has.
            case PaymentStatus.Authorized:
                return AuthorizePaymentResponse.Authorized(payment.Id);

            // The gateway never answered last time. Reuse the row, and therefore
            // the key, so this asks the same question rather than a second one.
            case PaymentStatus.TimedOut:
                payment.Retry(utcNow);
                break;

            // A previous attempt was recorded and then interrupted — a crash, or a
            // request still in flight. Either way the key is already there to be
            // asked under, and the gateway is the one that knows.
            case PaymentStatus.Pending:
                break;

            case null:
                payment = Begin(request, utcNow);
                _payments.Payments.Add(payment);
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(request), payment.Status, "Unmapped payment status.");
        }

        try
        {
            await _payments.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsDuplicateLiveAttempt(ex))
        {
            // Two confirms for one order, racing. The index said no, which is the
            // arrangement working; the loser must not now call the gateway.
            return AuthorizePaymentResponse.ConcurrentAttemptInFlight;
        }

        var (outcome, reference) = await _gateway
            .AuthorizeAsync(payment.IdempotencyKey, payment.Amount, payment.Currency, cancellationToken)
            .ConfigureAwait(false);

        // Read the clock again: the gateway is deliberately slow, and stamping an
        // answer with the time the question was asked would misreport it.
        var answeredAt = _clock.GetUtcNow().UtcDateTime;

        switch (outcome)
        {
            case GatewayOutcome.Succeeded:
                payment.Authorize(reference!, answeredAt);
                break;

            case GatewayOutcome.Declined:
                payment.Decline(answeredAt);
                break;

            case GatewayOutcome.TimedOut:
                payment.TimeOut(answeredAt);
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(request), outcome, "Unmapped gateway outcome.");
        }

        await _payments.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return outcome switch
        {
            GatewayOutcome.Succeeded => AuthorizePaymentResponse.Authorized(payment.Id),
            GatewayOutcome.Declined => AuthorizePaymentResponse.Declined(payment.Id),
            _ => AuthorizePaymentResponse.TimedOut(payment.Id)
        };
    }

    /// <inheritdoc />
    public async Task<CapturePaymentResponse> CaptureAsync(
        CapturePaymentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payment = await LiveAsync(request.OrderId, request.ClientId, cancellationToken)
            .ConfigureAwait(false);

        // Idempotent: a retried confirm after a dropped response must not tell a
        // customer their completed payment failed.
        if (payment?.Status is PaymentStatus.Captured)
        {
            return CapturePaymentResponse.Captured(payment.Id);
        }

        // Pending and TimedOut land here too, and correctly: neither has a gateway
        // reference, so there is nothing this module can ask to be captured.
        if (payment?.Status is not PaymentStatus.Authorized)
        {
            return CapturePaymentResponse.NoAuthorization;
        }

        var outcome = await _gateway
            .CaptureAsync(payment.GatewayReference!, cancellationToken)
            .ConfigureAwait(false);

        if (outcome is GatewayOutcome.TimedOut)
        {
            // Deliberately no state change. The funds are still held and the
            // attempt is still capturable, which is exactly what the row already
            // says — writing TimedOut here would throw away the reference and make
            // the retry impossible.
            return CapturePaymentResponse.TimedOut(payment.Id);
        }

        payment.Capture(_clock.GetUtcNow().UtcDateTime);

        try
        {
            await _payments.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A cancel got to this row first. Do not call the gateway again — it
            // has already been asked, and asking twice under the same key would
            // only get the same answer more slowly. Read what actually happened
            // and say that.
            await ReloadAsync(payment, cancellationToken).ConfigureAwait(false);

            return payment.Status is PaymentStatus.Captured
                ? CapturePaymentResponse.Captured(payment.Id)
                : CapturePaymentResponse.NoAuthorization;
        }

        return CapturePaymentResponse.Captured(payment.Id);
    }

    /// <inheritdoc />
    public async Task<VoidPaymentResponse> VoidAsync(
        VoidPaymentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payment = await LiveAsync(request.OrderId, request.ClientId, cancellationToken)
            .ConfigureAwait(false);

        if (payment?.Status is PaymentStatus.Captured)
        {
            return VoidPaymentResponse.AlreadyCaptured(payment.Id);
        }

        // Nothing live, nothing authorised, or an attempt already voided — all of
        // which mean the same thing to a caller unwinding a failed confirm: there
        // is nothing to unwind.
        if (payment?.Status is not PaymentStatus.Authorized)
        {
            return VoidPaymentResponse.NoAuthorization;
        }

        var outcome = await _gateway
            .VoidAsync(payment.GatewayReference!, cancellationToken)
            .ConfigureAwait(false);

        if (outcome is GatewayOutcome.TimedOut)
        {
            // Left Authorized on purpose. An authorisation nobody captures lapses
            // at the gateway on its own, so the benign outcome needs no bookkeeping
            // here — this is the half of 028 that makes authorise-then-sell worth
            // its extra round trip.
            return VoidPaymentResponse.TimedOut(payment.Id);
        }

        payment.Void(_clock.GetUtcNow().UtcDateTime);

        try
        {
            await _payments.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A confirm got to this row first, and this is the race the token
            // exists for: without it, this save would have written Voided over
            // money that was taken.
            await ReloadAsync(payment, cancellationToken).ConfigureAwait(false);

            return payment.Status is PaymentStatus.Captured
                ? VoidPaymentResponse.AlreadyCaptured(payment.Id)
                : VoidPaymentResponse.Voided(payment.Id);
        }

        return VoidPaymentResponse.Voided(payment.Id);
    }

    /// <summary>
    /// Refreshes a tracked entity from the database after losing a race, so what is
    /// read back is the winner's state rather than this request's rejected one.
    /// </summary>
    /// <remarks>
    /// The same move <c>EfSeatRepository</c> makes for the same reason (009):
    /// without it EF's identity map hands back the stale instance, and the answer
    /// would be this request's wishful thinking rather than the fact.
    /// </remarks>
    private async Task ReloadAsync(Payment payment, CancellationToken cancellationToken)
    {
        var entry = _payments.Entry(payment);

        entry.State = EntityState.Unchanged;
        await entry.ReloadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The one attempt against this order that could still be holding or have taken
    /// money, if there is one.
    /// </summary>
    /// <remarks>
    /// The client id is part of the predicate rather than checked afterwards, so a
    /// mismatch is indistinguishable from no such payment — the reasoning 011 gives
    /// for seat commands carrying an event id.
    /// </remarks>
    private Task<Payment?> LiveAsync(Guid orderId, Guid clientId, CancellationToken cancellationToken) =>
        _payments.Payments
            .SingleOrDefaultAsync(
                payment => payment.OrderId == orderId
                    && payment.ClientId == clientId
                    && Payment.LiveStatuses.Contains(payment.Status),
                cancellationToken);

    /// <summary>
    /// Opens a fresh attempt, with a key that names the order it belongs to.
    /// </summary>
    /// <remarks>
    /// Derived from the two ids rather than random, so the key is reproducible from
    /// the row and legible in a gateway's logs when somebody has to chase a payment
    /// by hand. The property that actually matters — a retry asking under the same
    /// key — comes from reusing the row, not from how the key was built.
    /// </remarks>
    private static Payment Begin(AuthorizePaymentRequest request, DateTime utcNow)
    {
        var paymentId = Guid.NewGuid();

        return Payment.Create(
            paymentId,
            request.OrderId,
            request.ClientId,
            request.Amount,
            request.Currency,
            $"order-{request.OrderId:N}-{paymentId:N}",
            utcNow);
    }

    /// <summary>
    /// Whether this save failed on the one-live-attempt index specifically, rather
    /// than on some other unique constraint that would be a real bug.
    /// </summary>
    private static bool IsDuplicateLiveAttempt(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: "23505" } postgres
        && postgres.ConstraintName == LiveAttemptIndex;
}
