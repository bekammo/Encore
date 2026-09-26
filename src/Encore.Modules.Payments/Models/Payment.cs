namespace Encore.Modules.Payments.Models;

/// <summary>
/// Factory and guarded transitions because a payment has rules decidable from its own row, yet
/// it stays in the flat module with no ports or domain assembly (013).
/// </summary>
public sealed class Payment
{
    // EF Core materialisation only.
    private Payment()
    {
    }

    private Payment(
        Guid id,
        Guid orderId,
        Guid clientId,
        decimal amount,
        string currency,
        string idempotencyKey,
        DateTime utcNow)
    {
        Id = id;
        OrderId = orderId;
        ClientId = clientId;
        Amount = amount;
        Currency = currency;
        IdempotencyKey = idempotencyKey;
        Status = PaymentStatus.Pending;
        AttemptedAt = utcNow;
    }

    public static Payment Create(
        Guid id,
        Guid orderId,
        Guid clientId,
        decimal amount,
        string currency,
        string idempotencyKey,
        DateTime utcNow)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A payment's id must not be empty.", nameof(id));
        }

        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("A payment must belong to an order.", nameof(orderId));
        }

        if (clientId == Guid.Empty)
        {
            throw new ArgumentException("A payment must be owed by a client.", nameof(clientId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);

        if (currency is not { Length: 3 })
        {
            throw new ArgumentException(
                "Currency must be a three-letter ISO 4217 code.", nameof(currency));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ArgumentException(
                "An idempotency key is required.", nameof(idempotencyKey));
        }

        GuardUtc(utcNow);

        return new Payment(id, orderId, clientId, amount, currency, idempotencyKey, utcNow);
    }

    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    public Guid ClientId { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public PaymentStatus Status { get; private set; }

    public string IdempotencyKey { get; private set; } = string.Empty;

    public string? GatewayReference { get; private set; }

    /// <summary>When the gateway was last asked, or when it granted the authorisation.</summary>
    public DateTime AttemptedAt { get; private set; }

    /// <summary>Set by a timeout or an ending; null while pending or authorised.</summary>
    public DateTime? ResolvedAt { get; private set; }

    public uint RowVersion { get; private set; }

    /// <summary>
    /// Statuses that might hold or have taken money, so <see cref="PaymentStatus.TimedOut"/> is in:
    /// no answer may mean yes (013). The unique index's filter is built from this list, so a
    /// change to it needs a migration.
    /// </summary>
    public static IReadOnlyList<PaymentStatus> LiveStatuses { get; } =
    [
        PaymentStatus.Pending,
        PaymentStatus.Authorized,
        PaymentStatus.Captured,
        PaymentStatus.TimedOut
    ];

    public bool IsLive => LiveStatuses.Contains(Status);

    /// <exception cref="PaymentTransitionException">The attempt is no longer pending.</exception>
    public void Authorize(string gatewayReference, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayReference);
        GuardUtc(utcNow);
        GuardPending();

        Status = PaymentStatus.Authorized;
        GatewayReference = gatewayReference;
        AttemptedAt = utcNow;
    }

    /// <exception cref="PaymentTransitionException">The attempt is no longer pending.</exception>
    public void Decline(DateTime utcNow)
    {
        GuardUtc(utcNow);
        GuardPending();

        Status = PaymentStatus.Declined;
        ResolvedAt = utcNow;
    }

    /// <summary>
    /// Authorisations only: a timed-out capture stays <see cref="PaymentStatus.Authorized"/> (014).
    /// </summary>
    /// <exception cref="PaymentTransitionException">The attempt is no longer pending.</exception>
    public void TimeOut(DateTime utcNow)
    {
        GuardUtc(utcNow);
        GuardPending();

        Status = PaymentStatus.TimedOut;
        ResolvedAt = utcNow;
    }

    /// <exception cref="PaymentTransitionException">The attempt did get an answer.</exception>
    public void Retry(DateTime utcNow)
    {
        GuardUtc(utcNow);
        GuardTimedOut();

        Status = PaymentStatus.Pending;
        ResolvedAt = null;
        AttemptedAt = utcNow;
    }

    /// <summary>
    /// Restamps <see cref="AttemptedAt"/>, so the reconciler does not take a live attempt for one
    /// a crash left behind (022).
    /// </summary>
    /// <exception cref="PaymentTransitionException">The attempt is no longer pending.</exception>
    public void Resume(DateTime utcNow)
    {
        GuardUtc(utcNow);
        GuardPending();

        AttemptedAt = utcNow;
    }

    /// <exception cref="PaymentTransitionException">There is no live authorisation.</exception>
    public void Capture(DateTime utcNow)
    {
        GuardUtc(utcNow);

        if (Status is PaymentStatus.Captured)
        {
            return;
        }

        GuardAuthorized();

        Status = PaymentStatus.Captured;
        ResolvedAt = utcNow;
    }

    /// <summary>
    /// The gateway refused to take the money it had authorised, so nothing is held any more (034).
    /// Not live, so the order's next confirm starts a fresh attempt.
    /// </summary>
    /// <exception cref="PaymentTransitionException">
    /// The money has been taken, or there is no live authorisation.
    /// </exception>
    public void DeclineCapture(DateTime utcNow)
    {
        GuardUtc(utcNow);

        if (Status is PaymentStatus.Captured)
        {
            throw new PaymentTransitionException(Id, PaymentTransitionReason.AlreadyCaptured);
        }

        GuardAuthorized();

        Status = PaymentStatus.Declined;
        ResolvedAt = utcNow;
    }

    /// <exception cref="PaymentTransitionException">
    /// The money has been taken, or there was never an authorisation.
    /// </exception>
    public void Void(DateTime utcNow)
    {
        GuardUtc(utcNow);

        if (Status is PaymentStatus.Voided)
        {
            return;
        }

        if (Status is PaymentStatus.Captured)
        {
            throw new PaymentTransitionException(Id, PaymentTransitionReason.AlreadyCaptured);
        }

        GuardAuthorized();

        Status = PaymentStatus.Voided;
        ResolvedAt = utcNow;
    }

    // Looked-up answers get their own transitions (014), so the ordinary path cannot write one
    // it never received.

    /// <summary>
    /// Only once the gateway has released the funds, so no unowned authorisation is left.
    /// </summary>
    /// <exception cref="PaymentTransitionException">The attempt is not timed out.</exception>
    public void ResolveAsVoided(string gatewayReference, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayReference);
        GuardUtc(utcNow);
        GuardTimedOut();

        Status = PaymentStatus.Voided;
        GatewayReference = gatewayReference;
        ResolvedAt = utcNow;
    }

    /// <exception cref="PaymentTransitionException">The attempt is not timed out.</exception>
    public void ResolveAsDeclined(DateTime utcNow)
    {
        GuardUtc(utcNow);
        GuardTimedOut();

        Status = PaymentStatus.Declined;
        ResolvedAt = utcNow;
    }

    /// <exception cref="PaymentTransitionException">The attempt is not timed out.</exception>
    public void ResolveAsAbandoned(DateTime utcNow)
    {
        GuardUtc(utcNow);
        GuardTimedOut();

        Status = PaymentStatus.Abandoned;
        ResolvedAt = utcNow;
    }

    private static void GuardUtc(DateTime utcNow)
    {
        if (utcNow.Kind is not DateTimeKind.Utc)
        {
            throw new ArgumentException(
                $"utcNow must be a UTC instant; its Kind was {utcNow.Kind}. Time enters this system once, through TimeProvider.GetUtcNow().UtcDateTime.",
                nameof(utcNow));
        }
    }

    private void GuardPending()
    {
        if (Status is not PaymentStatus.Pending)
        {
            throw new PaymentTransitionException(Id, PaymentTransitionReason.NotPending);
        }
    }

    private void GuardAuthorized()
    {
        if (Status is not PaymentStatus.Authorized)
        {
            throw new PaymentTransitionException(Id, PaymentTransitionReason.NotAuthorized);
        }
    }

    private void GuardTimedOut()
    {
        if (Status is not PaymentStatus.TimedOut)
        {
            throw new PaymentTransitionException(Id, PaymentTransitionReason.NotTimedOut);
        }
    }
}
