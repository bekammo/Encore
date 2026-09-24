using Encore.Modules.Payments.Contracts;
using Encore.Modules.Payments.Models;
using Encore.Modules.Payments.Simulation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Encore.Modules.Payments.Data;

/// <summary>
/// The row is written before the gateway is called (013): after a crash the next attempt asks
/// again under the same key instead of minting one that could authorise twice. Once the row is
/// committed, the gateway call and the save of its answer ignore the caller's token (022).
/// </summary>
internal sealed class InProcessOrderPayments(
    PaymentsDbContext payments,
    SimulatedPaymentGateway gateway,
    TimeProvider timeProvider) : IOrderPayments
{
    private readonly PaymentsDbContext _payments = payments;
    private readonly SimulatedPaymentGateway _gateway = gateway;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <inheritdoc />
    public async Task<AuthorizePaymentResponse> AuthorizeAsync(
        AuthorizePaymentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payment = await LiveAsync(request.OrderId, request.ClientId, cancellationToken)
            .ConfigureAwait(false);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        switch (payment?.Status)
        {
            case PaymentStatus.Captured:
                return AuthorizePaymentResponse.AlreadyCaptured(payment.Id);

            case PaymentStatus.Authorized:
                return AuthorizePaymentResponse.Authorized(payment.Id);

            case PaymentStatus.TimedOut:
                payment.Retry(utcNow);
                break;

            // Forced: at the same instant Resume changes nothing, and the write is what lets
            // xmin order this against another confirm or the reconciler (022).
            case PaymentStatus.Pending:
                payment.Resume(utcNow);
                _payments.Entry(payment).Property(attempt => attempt.AttemptedAt).IsModified = true;
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
            return AuthorizePaymentResponse.ConcurrentAttemptInFlight;
        }
        catch (DbUpdateException ex) when (IsDuplicateLiveAttempt(ex))
        {
            return AuthorizePaymentResponse.ConcurrentAttemptInFlight;
        }

        var (outcome, reference) = await _gateway
            .AuthorizeAsync(payment.IdempotencyKey, payment.Amount, payment.Currency, CancellationToken.None)
            .ConfigureAwait(false);

        var answeredAt = _timeProvider.GetUtcNow().UtcDateTime;

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

        try
        {
            await _payments.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another confirm resumed this attempt meanwhile, and the same key got the same
            // decision: report the row as the winner left it.
            await ReloadAsync(payment, CancellationToken.None).ConfigureAwait(false);

            return AnswerFor(payment);
        }

        return outcome switch
        {
            GatewayOutcome.Succeeded => AuthorizePaymentResponse.Authorized(payment.Id),
            GatewayOutcome.Declined => AuthorizePaymentResponse.Declined(payment.Id),
            GatewayOutcome.TimedOut => AuthorizePaymentResponse.TimedOut(payment.Id)
        };
    }

    // Not yet answered, or settled by the reconciler meanwhile: left for the next confirm.
    private static AuthorizePaymentResponse AnswerFor(Payment payment) =>
        payment.Status switch
        {
            PaymentStatus.Authorized => AuthorizePaymentResponse.Authorized(payment.Id),
            PaymentStatus.Captured => AuthorizePaymentResponse.AlreadyCaptured(payment.Id),
            PaymentStatus.Declined => AuthorizePaymentResponse.Declined(payment.Id),
            PaymentStatus.TimedOut => AuthorizePaymentResponse.TimedOut(payment.Id),
            PaymentStatus.Pending or PaymentStatus.Voided or PaymentStatus.Abandoned =>
                AuthorizePaymentResponse.ConcurrentAttemptInFlight
        };

    /// <inheritdoc />
    public async Task<CapturePaymentResponse> CaptureAsync(
        CapturePaymentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payment = await LiveAsync(request.OrderId, request.ClientId, cancellationToken)
            .ConfigureAwait(false);

        if (payment?.Status is PaymentStatus.Captured)
        {
            return CapturePaymentResponse.Captured(payment.Id);
        }

        if (payment?.Status is not PaymentStatus.Authorized)
        {
            return CapturePaymentResponse.NoAuthorization;
        }

        var outcome = await _gateway
            .CaptureAsync(payment.GatewayReference!, CancellationToken.None)
            .ConfigureAwait(false);

        if (outcome is GatewayOutcome.TimedOut)
        {
            return CapturePaymentResponse.TimedOut(payment.Id);
        }

        payment.Capture(_timeProvider.GetUtcNow().UtcDateTime);

        try
        {
            await _payments.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            await ReloadAsync(payment, CancellationToken.None).ConfigureAwait(false);

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

        // Pending and TimedOut have no reference to void; the reconciler releases whatever they
        // turn out to hold (014, 022).
        if (payment?.Status is not PaymentStatus.Authorized)
        {
            return VoidPaymentResponse.NoAuthorization;
        }

        var outcome = await _gateway
            .VoidAsync(payment.GatewayReference!, CancellationToken.None)
            .ConfigureAwait(false);

        if (outcome is GatewayOutcome.TimedOut)
        {
            return VoidPaymentResponse.TimedOut(payment.Id);
        }

        payment.Void(_timeProvider.GetUtcNow().UtcDateTime);

        try
        {
            await _payments.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A confirm got here first; xmin stopped this writing Voided over captured money.
            await ReloadAsync(payment, CancellationToken.None).ConfigureAwait(false);

            return payment.Status is PaymentStatus.Captured
                ? VoidPaymentResponse.AlreadyCaptured(payment.Id)
                : VoidPaymentResponse.Voided(payment.Id);
        }

        return VoidPaymentResponse.Voided(payment.Id);
    }

    private async Task ReloadAsync(Payment payment, CancellationToken cancellationToken)
    {
        var entry = _payments.Entry(payment);

        entry.State = EntityState.Unchanged;
        await entry.ReloadAsync(cancellationToken).ConfigureAwait(false);
    }

    // A client mismatch reads as no payment.
    private Task<Payment?> LiveAsync(Guid orderId, Guid clientId, CancellationToken cancellationToken) =>
        _payments.Payments
            .SingleOrDefaultAsync(
                payment => payment.OrderId == orderId
                    && payment.ClientId == clientId
                    && Payment.LiveStatuses.Contains(payment.Status),
                cancellationToken);

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

    private static bool IsDuplicateLiveAttempt(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
        && postgres.ConstraintName == PaymentConfiguration.LiveAttemptIndex;
}
