namespace Encore.Modules.Payments.Models;

/// <summary>
/// An attempt to charge for an order, and how it turned out. Owns which transition is
/// legal from which state.
/// </summary>
/// <remarks>
/// Factory construction and guarded transitions, because a payment has rules decidable from
/// its own row: only an authorised payment may be captured, captured is terminal, and the
/// amount never changes. It stays in the flat module with no ports or domain assembly. The
/// real guard against a double charge is the partial unique index in
/// <c>PaymentConfiguration</c>. Time is always passed in, and must be UTC.
/// </remarks>
public sealed class Payment
{
    /// <summary>For EF Core materialisation only.</summary>
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

    /// <summary>
    /// Begins an attempt. Every payment starts <see cref="PaymentStatus.Pending"/>. Invalid
    /// arguments throw BCL exceptions: they are caller bugs, not refusals.
    /// </summary>
    /// <param name="idempotencyKey">How the gateway recognises a repeat. Kept for the life of the row.</param>
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

    /// <summary>The order being paid for. At most one live attempt per order.</summary>
    public Guid OrderId { get; private set; }

    /// <summary>Who is paying: the claimed <c>X-Client-Id</c>.</summary>
    public Guid ClientId { get; private set; }

    /// <summary>What is owed, fixed when the attempt began.</summary>
    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public PaymentStatus Status { get; private set; }

    /// <summary>How the gateway recognises a repeat. Survives <see cref="Retry"/>.</summary>
    public string IdempotencyKey { get; private set; } = string.Empty;

    /// <summary>The gateway's handle on the authorisation, once there is one.</summary>
    public string? GatewayReference { get; private set; }

    public DateTime AttemptedAt { get; private set; }

    /// <summary>
    /// When the attempt got its answer: an ending, or a timeout. A timed-out attempt still has
    /// moves left, and the reconciler ages it by this. Null while pending or authorised.
    /// </summary>
    public DateTime? ResolvedAt { get; private set; }

    /// <summary>Concurrency token (<c>xmin</c>). A confirm and a cancel can race this row.</summary>
    public uint RowVersion { get; private set; }

    /// <summary>
    /// Statuses in which the attempt might hold or have taken money. <see cref="PaymentStatus.TimedOut"/>
    /// is included: no answer may mean yes. The unique index's filter is built from this list,
    /// so a change to it is a model change that needs a migration.
    /// </summary>
    public static IReadOnlyList<PaymentStatus> LiveStatuses { get; } =
    [
        PaymentStatus.Pending,
        PaymentStatus.Authorized,
        PaymentStatus.Captured,
        PaymentStatus.TimedOut
    ];

    /// <summary>Whether this attempt might hold or have taken money. Not mapped.</summary>
    public bool IsLive => LiveStatuses.Contains(Status);

    /// <summary>The gateway is holding the funds. Not an ending: a capture or void is still owed.</summary>
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

    /// <summary>The gateway refused. Terminal.</summary>
    /// <exception cref="PaymentTransitionException">The attempt is no longer pending.</exception>
    public void Decline(DateTime utcNow)
    {
        GuardUtc(utcNow);
        GuardPending();

        Status = PaymentStatus.Declined;
        ResolvedAt = utcNow;
    }

    /// <summary>
    /// The authorisation got no answer. Only authorisations reach this; a capture that
    /// times out stays <see cref="PaymentStatus.Authorized"/>.
    /// </summary>
    /// <exception cref="PaymentTransitionException">The attempt is no longer pending.</exception>
    public void TimeOut(DateTime utcNow)
    {
        GuardUtc(utcNow);
        GuardPending();

        Status = PaymentStatus.TimedOut;
        ResolvedAt = utcNow;
    }

    /// <summary>
    /// Re-opens a timed-out attempt on the same row, so the gateway is asked again under the
    /// same key rather than authorising twice.
    /// </summary>
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
    /// Asks again about an attempt that was recorded but never answered, under the same key.
    /// Restamps <see cref="AttemptedAt"/>, so the reconciler sees a live attempt rather than
    /// one a crash abandoned, and the save is a write that <c>xmin</c> can order.
    /// </summary>
    /// <exception cref="PaymentTransitionException">The attempt is no longer pending.</exception>
    public void Resume(DateTime utcNow)
    {
        GuardUtc(utcNow);
        GuardPending();

        AttemptedAt = utcNow;
    }

    /// <summary>Takes the authorised money. Terminal and idempotent.</summary>
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

    /// <summary>Releases an authorisation without taking the money. Terminal and idempotent.</summary>
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

    /// <summary>
    /// Reconciliation: the timed-out authorisation did land, and its funds have now been
    /// released. Called only after the release succeeded, so no unowned authorisation is left.
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

    /// <summary>Reconciliation: the timed-out authorisation reached the gateway and was refused.</summary>
    /// <exception cref="PaymentTransitionException">The attempt is not timed out.</exception>
    public void ResolveAsDeclined(DateTime utcNow)
    {
        GuardUtc(utcNow);
        GuardTimedOut();

        Status = PaymentStatus.Declined;
        ResolvedAt = utcNow;
    }

    /// <summary>
    /// Reconciliation: the gateway has no record, so the request never arrived. Frees the
    /// order's live-attempt slot.
    /// </summary>
    /// <exception cref="PaymentTransitionException">The attempt is not timed out.</exception>
    public void ResolveAsAbandoned(DateTime utcNow)
    {
        GuardUtc(utcNow);
        GuardTimedOut();

        Status = PaymentStatus.Abandoned;
        ResolvedAt = utcNow;
    }

    /// <summary>Every transition requires a UTC instant; <c>Local</c> and <c>Unspecified</c> are refused.</summary>
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

    /// <summary>For <see cref="Retry"/> and the reconciliation transitions: only an unanswered attempt qualifies.</summary>
    private void GuardTimedOut()
    {
        if (Status is not PaymentStatus.TimedOut)
        {
            throw new PaymentTransitionException(Id, PaymentTransitionReason.NotTimedOut);
        }
    }
}
