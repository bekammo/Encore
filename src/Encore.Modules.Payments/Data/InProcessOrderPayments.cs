using Encore.Modules.Payments.Contracts;
using Encore.Modules.Payments.Models;
using Encore.Modules.Payments.Simulation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Encore.Modules.Payments.Data;

/// <summary>
/// Serves <see cref="IOrderPayments"/> in process, against this module's tables and its
/// simulated gateway.
/// </summary>
/// <remarks>
/// The row is always written before the gateway is called. After a crash in between, the
/// next attempt finds the row and asks again under the same idempotency key instead of
/// minting a new one, which could authorise twice.
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
            // Already paid: an answer, not a refusal.
            case PaymentStatus.Captured:
                return AuthorizePaymentResponse.AlreadyCaptured(payment.Id);

            // Already held: idempotent.
            case PaymentStatus.Authorized:
                return AuthorizePaymentResponse.Authorized(payment.Id);

            // No answer last time: reuse the row and its key.
            case PaymentStatus.TimedOut:
                payment.Retry(utcNow);
                break;

            // Recorded then interrupted: ask again under the existing key.
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
        catch (DbUpdateConcurrencyException)
        {
            // The reconciler settled this attempt meanwhile. Caught before the next clause,
            // which filters a base type. Retryable.
            return AuthorizePaymentResponse.ConcurrentAttemptInFlight;
        }
        catch (DbUpdateException ex) when (IsDuplicateLiveAttempt(ex))
        {
            // Two confirms raced; the index refused this one, which must not call the gateway.
            return AuthorizePaymentResponse.ConcurrentAttemptInFlight;
        }

        var (outcome, reference) = await _gateway
            .AuthorizeAsync(payment.IdempotencyKey, payment.Amount, payment.Currency, cancellationToken)
            .ConfigureAwait(false);

        // Read the clock again: the gateway is slow.
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

        // Idempotent.
        if (payment?.Status is PaymentStatus.Captured)
        {
            return CapturePaymentResponse.Captured(payment.Id);
        }

        // Includes Pending and TimedOut: neither has a gateway reference to capture.
        if (payment?.Status is not PaymentStatus.Authorized)
        {
            return CapturePaymentResponse.NoAuthorization;
        }

        var outcome = await _gateway
            .CaptureAsync(payment.GatewayReference!, cancellationToken)
            .ConfigureAwait(false);

        if (outcome is GatewayOutcome.TimedOut)
        {
            // No state change: the funds are still held and the capture can be retried.
            return CapturePaymentResponse.TimedOut(payment.Id);
        }

        payment.Capture(_clock.GetUtcNow().UtcDateTime);

        try
        {
            await _payments.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A cancel got here first. Report what actually happened.
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

        // Nothing to release.
        if (payment?.Status is not PaymentStatus.Authorized)
        {
            return VoidPaymentResponse.NoAuthorization;
        }

        var outcome = await _gateway
            .VoidAsync(payment.GatewayReference!, cancellationToken)
            .ConfigureAwait(false);

        if (outcome is GatewayOutcome.TimedOut)
        {
            // Left Authorized: an uncaptured authorisation lapses at the gateway on its own.
            return VoidPaymentResponse.TimedOut(payment.Id);
        }

        payment.Void(_clock.GetUtcNow().UtcDateTime);

        try
        {
            await _payments.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A confirm got here first; xmin stopped this writing Voided over captured money.
            await ReloadAsync(payment, cancellationToken).ConfigureAwait(false);

            return payment.Status is PaymentStatus.Captured
                ? VoidPaymentResponse.AlreadyCaptured(payment.Id)
                : VoidPaymentResponse.Voided(payment.Id);
        }

        return VoidPaymentResponse.Voided(payment.Id);
    }

    /// <summary>
    /// Reloads a tracked entity after a lost race, so the winner's state is read back.
    /// </summary>
    private async Task ReloadAsync(Payment payment, CancellationToken cancellationToken)
    {
        var entry = _payments.Entry(payment);

        entry.State = EntityState.Unchanged;
        await entry.ReloadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The live attempt for this order and client, if any. A client mismatch looks like no payment.
    /// </summary>
    private Task<Payment?> LiveAsync(Guid orderId, Guid clientId, CancellationToken cancellationToken) =>
        _payments.Payments
            .SingleOrDefaultAsync(
                payment => payment.OrderId == orderId
                    && payment.ClientId == clientId
                    && Payment.LiveStatuses.Contains(payment.Status),
                cancellationToken);

    /// <summary>
    /// Opens a fresh attempt. The key embeds the ids, so it is readable in gateway logs.
    /// </summary>
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
    /// Whether the save failed on the one-live-attempt index, not some other constraint.
    /// </summary>
    private static bool IsDuplicateLiveAttempt(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: "23505" } postgres
        && postgres.ConstraintName == LiveAttemptIndex;
}
