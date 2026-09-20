namespace Encore.Modules.Payments.Models;

/// <summary>
/// An attempt to charge for an order, and how that attempt turned out. Owns the
/// rules about which move is legal from which state, so no caller can reach a
/// state the rules never approved.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a plain persistence POCO, and not the start of a hexagon either.</b>
/// <c>Event</c>, <c>Venue</c> and <c>Order</c> have public setters because they
/// have no local invariants — every rule about an order spans its row and
/// Inventory's. A payment is different: "only an authorised payment may be
/// captured", "captured is terminal" and "the amount never changes after the
/// gateway was asked" are all decidable from this row alone, so they are enforced
/// here. That is <c>DECISIONS.md</c> 005's factory rule, which is a separate
/// argument from 001's hexagon; this module takes the first without the second,
/// and gets no <c>Ports/</c>, no <c>Adapters/</c> and no domain assembly. See 029.
/// </para>
/// <para>
/// <b>The real guard against a double charge is in Postgres, not here.</b> This
/// class is the readable expression of the rules; the partial unique index in
/// <c>PaymentConfiguration</c> is what actually stops a second live attempt
/// against one order, the same way <c>ux_orders_client_event_pending</c> does for
/// checkouts and <c>xmin</c> does for seats. Nothing in this file would survive
/// two processes racing, and nothing in it needs to.
/// </para>
/// <para>
/// <b>No domain events.</b> <c>Seat</c> raises them because hold history has to be
/// reconstructable and because the outbox will one day carry <c>SeatSold</c> out
/// of the module. Nothing subscribes to a payment: Orders reads this row directly,
/// in process. Events with no consumer would be the ceremony 001 argues against,
/// and they can be added the day the outbox arrives. See 029.
/// </para>
/// <para>
/// Time is a parameter, never a reading — as it is for <c>Seat</c>, and since
/// 045 every method that takes one checks that what arrived is UTC, exactly as
/// <c>Seat</c> has since 039. That sentence used to be a claim about callers;
/// it is now a precondition of this type.
/// </para>
/// </remarks>
public sealed class Payment
{
    /// <summary>
    /// For EF Core's materialisation pipeline only. Private rather than protected
    /// because <see cref="Payment"/> is sealed.
    /// </summary>
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
    /// The only way application code can begin an attempt. Every payment that
    /// exists was born <see cref="PaymentStatus.Pending"/> and reached anything
    /// else through a transition below.
    /// </summary>
    /// <remarks>
    /// Every guard here is an argument check rather than a domain refusal: an
    /// empty identity, a non-positive amount, a currency that is not a
    /// three-letter code, a blank idempotency key and a non-UTC instant are all
    /// incoherent requests rather than states of the world, so they throw the BCL
    /// exceptions a caller would expect. The values arrive from an order that
    /// copied them from an event, which validated them at the HTTP edge — this is
    /// the belt to that's braces, and it is cheap.
    /// <para>
    /// They read in parameter order, which is why the <paramref name="utcNow"/>
    /// check comes last here while the transition methods guard it first: there,
    /// it is the significant precondition and the state guard follows it, as in
    /// <c>Seat</c>.
    /// </para>
    /// </remarks>
    /// <param name="id">Identity for the attempt, assigned by the caller.</param>
    /// <param name="orderId">The order being paid for.</param>
    /// <param name="clientId">Who is paying, as claimed by <c>X-Client-Id</c>.</param>
    /// <param name="amount">What is owed. Positive.</param>
    /// <param name="currency">ISO 4217 code for <paramref name="amount"/>.</param>
    /// <param name="idempotencyKey">
    /// What the gateway uses to recognise a repeat of this attempt. Kept for the
    /// life of the row, including across <see cref="Retry"/>.
    /// </param>
    /// <param name="utcNow">The instant the attempt began.</param>
    public static Payment Create(
        Guid id,
        Guid orderId,
        Guid clientId,
        decimal amount,
        string currency,
        string idempotencyKey,
        DateTime utcNow)
    {
        // DECISIONS 045, taking 038 to the other factory-constructed type. An
        // empty Guid is not an identity: an attempt with one cannot be addressed,
        // an attempt against order Guid.Empty belongs to no order, and one owed by
        // client Guid.Empty is owed by nobody. All three are states no rule ever
        // approved, reachable through the one door 029 built to prevent them.
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

    /// <summary>Identity of this attempt.</summary>
    public Guid Id { get; private set; }

    /// <summary>The order being paid for. One live attempt per order, at most.</summary>
    public Guid OrderId { get; private set; }

    /// <summary>
    /// Who is paying. The same claimed identity Orders and Inventory use —
    /// <c>X-Client-Id</c>, not an authenticated user, because there is no Identity
    /// module yet (<c>DECISIONS.md</c> 014).
    /// </summary>
    public Guid ClientId { get; private set; }

    /// <summary>
    /// What is owed, snapshotted when the attempt began and never changed
    /// afterwards. A decimal and a currency code rather than a value object, per
    /// <c>DECISIONS.md</c> 016.
    /// </summary>
    public decimal Amount { get; private set; }

    /// <summary>ISO 4217 code for <see cref="Amount"/>, copied from the order.</summary>
    public string Currency { get; private set; } = string.Empty;

    /// <summary>Where this attempt has got to.</summary>
    public PaymentStatus Status { get; private set; }

    /// <summary>
    /// What the gateway uses to recognise a repeat of this attempt. Survives
    /// <see cref="Retry"/> deliberately: reusing the key is the whole reason a
    /// timed-out authorisation reuses this row rather than starting a new one.
    /// </summary>
    public string IdempotencyKey { get; private set; } = string.Empty;

    /// <summary>
    /// The gateway's own handle on the authorisation, once there is one. Null
    /// until <see cref="Authorize"/>, and the thing a human needs when a payment
    /// has to be chased by hand.
    /// </summary>
    public string? GatewayReference { get; private set; }

    /// <summary>When the current attempt was made. Always UTC.</summary>
    public DateTime AttemptedAt { get; private set; }

    /// <summary>
    /// When this attempt reached a state nothing more can happen from, if it has.
    /// Null while <see cref="PaymentStatus.Pending"/> or
    /// <see cref="PaymentStatus.Authorized"/>, because both of those still have a
    /// move left.
    /// </summary>
    public DateTime? ResolvedAt { get; private set; }

    /// <summary>
    /// Concurrency token, mapped to the Postgres <c>xmin</c> system column as
    /// <c>Seat</c>'s and <c>Order</c>'s are.
    /// </summary>
    /// <remarks>
    /// Payments is a module over uncontended tables with one exception, and it is
    /// the same exception Orders has: confirm and cancel arriving together — one
    /// impatient double-click — really do race this row, and they race it towards
    /// <i>different</i> answers. Without a token the loser can write
    /// <see cref="PaymentStatus.Voided"/> over an attempt whose money has already
    /// been taken, and the record would then say nobody was charged. The in-memory
    /// guard in <see cref="Void"/> cannot catch that, because both callers loaded
    /// the row while it still read <see cref="PaymentStatus.Authorized"/>.
    /// </remarks>
    public uint RowVersion { get; private set; }

    /// <summary>
    /// The statuses in which an attempt could still be holding, or have taken, the
    /// customer's money. One definition, used by <see cref="IsLive"/> in memory and
    /// by the adapter's query in SQL.
    /// </summary>
    /// <remarks>
    /// <see cref="PaymentStatus.TimedOut"/> is in here because the honest reading
    /// of "the gateway never answered" is "possibly yes". Excluding it would let a
    /// retry authorise a second time against a gateway that did receive the first
    /// call. The partial unique index has to repeat this list as a SQL literal,
    /// because an index filter cannot call into C# — the two are pinned together by
    /// <c>PaymentTests.IsLive_ShouldMatchTheIndexFilter</c> and by nothing else.
    /// </remarks>
    public static IReadOnlyList<PaymentStatus> LiveStatuses { get; } =
    [
        PaymentStatus.Pending,
        PaymentStatus.Authorized,
        PaymentStatus.Captured,
        PaymentStatus.TimedOut
    ];

    /// <summary>
    /// Whether this attempt could still be holding, or have taken, the customer's
    /// money. Computed, so it has no column — <c>PaymentConfiguration</c> ignores it.
    /// </summary>
    public bool IsLive => LiveStatuses.Contains(Status);

    /// <summary>
    /// Records that the gateway is holding the funds.
    /// </summary>
    /// <remarks>
    /// Does not set <see cref="ResolvedAt"/>: an authorisation is not an ending,
    /// it is the state in which a capture or a void is still owed.
    /// </remarks>
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

    /// <summary>
    /// Records that the gateway refused. Terminal, and definitively moved nothing.
    /// </summary>
    /// <exception cref="PaymentTransitionException">The attempt is no longer pending.</exception>
    public void Decline(DateTime utcNow)
    {
        GuardUtc(utcNow);
        GuardPending();

        Status = PaymentStatus.Declined;
        ResolvedAt = utcNow;
    }

    /// <summary>
    /// Records that the authorisation got no answer, so whether funds are held is
    /// unknown.
    /// </summary>
    /// <remarks>
    /// Only an authorisation reaches this. A capture that times out leaves the
    /// payment <see cref="PaymentStatus.Authorized"/>, which is still exactly what
    /// is true — funds held, nothing captured — and is retryable as it stands.
    /// </remarks>
    /// <exception cref="PaymentTransitionException">The attempt is no longer pending.</exception>
    public void TimeOut(DateTime utcNow)
    {
        GuardUtc(utcNow);
        GuardPending();

        Status = PaymentStatus.TimedOut;
        ResolvedAt = utcNow;
    }

    /// <summary>
    /// Re-opens a timed-out attempt so it can be tried again, on the same row and
    /// therefore with the same idempotency key.
    /// </summary>
    /// <remarks>
    /// The alternative — a second row with a second key — is a double
    /// authorisation the moment the first call turns out to have arrived. Only
    /// <see cref="PaymentStatus.TimedOut"/> qualifies: every other status got an
    /// answer, and a customer retrying after a decline is starting a genuinely new
    /// attempt, possibly with a different card. See <c>DECISIONS.md</c> 031.
    /// </remarks>
    /// <exception cref="PaymentTransitionException">The attempt did get an answer.</exception>
    public void Retry(DateTime utcNow)
    {
        GuardUtc(utcNow);

        if (Status is not PaymentStatus.TimedOut)
        {
            throw new PaymentTransitionException(Id, PaymentTransitionReason.NotTimedOut);
        }

        Status = PaymentStatus.Pending;
        ResolvedAt = null;
        AttemptedAt = utcNow;
    }

    /// <summary>
    /// Takes the money that was authorised. Terminal.
    /// </summary>
    /// <remarks>
    /// Idempotent for the same reason re-holding a seat is: a retried confirm
    /// after a dropped response must not tell a customer their completed payment
    /// failed. The resolution time does not move on the repeat, so the record
    /// still says when the money was actually taken.
    /// </remarks>
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
    /// Releases an authorisation without taking the money. Terminal.
    /// </summary>
    /// <remarks>
    /// The move that makes authorise-then-sell safe: when a seat cannot be sold,
    /// this puts the customer back where they started with nothing having left
    /// their account. Idempotent, like <see cref="Capture"/>.
    /// </remarks>
    /// <exception cref="PaymentTransitionException">
    /// The money has already been taken, or there was never an authorisation to
    /// release.
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
    /// Records that the timed-out authorisation did land, and that the funds it was
    /// holding have now been released. Terminal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One method for two facts, because a row between them would be a worse
    /// lie than either.</b> Reconciliation learns that funds are held and releases
    /// them in the same breath; writing <see cref="PaymentStatus.Authorized"/> in
    /// between would leave an authorisation nobody is going to capture if the
    /// process died there — a new orphan of exactly the kind this path exists to
    /// clear. Nothing is written until the release succeeded, so a failure leaves
    /// the row <see cref="PaymentStatus.TimedOut"/> and the next sweep tries again.
    /// </para>
    /// <para>
    /// <b>Releasing rather than capturing is <c>DECISIONS.md</c> 028 arriving
    /// late.</b> An authorisation that times out aborts the confirm before any seat
    /// is sold, so a <see cref="PaymentStatus.TimedOut"/> row never has seats behind
    /// it and 028's rule — a sale that does not complete voids — is the rule that
    /// applies. The void simply never happened, because nobody knew there was
    /// anything to void.
    /// </para>
    /// <para>
    /// <see cref="AttemptedAt"/> does not move. No attempt was made here; an answer
    /// was read back, and the funds were held when the original call reached the
    /// gateway rather than when this found out about it.
    /// </para>
    /// </remarks>
    /// <param name="gatewayReference">
    /// The handle the lookup returned, kept because the row never got one and it is
    /// what a human needs to chase this payment by hand.
    /// </param>
    /// <param name="utcNow">When reconciliation resolved the attempt.</param>
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

    /// <summary>
    /// Records that the timed-out authorisation reached the gateway and was
    /// refused. Terminal.
    /// </summary>
    /// <remarks>
    /// The one route to <see cref="PaymentStatus.Declined"/> that 031 did not
    /// reject. Its objection was to reading a silence as a refusal; this is a
    /// refusal read back from the gateway, which is the fact the silence was hiding.
    /// </remarks>
    /// <exception cref="PaymentTransitionException">The attempt is not timed out.</exception>
    public void ResolveAsDeclined(DateTime utcNow)
    {
        GuardUtc(utcNow);
        GuardTimedOut();

        Status = PaymentStatus.Declined;
        ResolvedAt = utcNow;
    }

    /// <summary>
    /// Records that the gateway has no knowledge of this attempt, so it never
    /// arrived and nothing was ever held. Terminal.
    /// </summary>
    /// <remarks>
    /// No <see cref="GatewayReference"/> is written, because there is nothing to
    /// refer to. This is the outcome that gives the order its live-attempt slot
    /// back, so the customer can try again without waiting for anybody.
    /// </remarks>
    /// <exception cref="PaymentTransitionException">The attempt is not timed out.</exception>
    public void ResolveAsAbandoned(DateTime utcNow)
    {
        GuardUtc(utcNow);
        GuardTimedOut();

        Status = PaymentStatus.Abandoned;
        ResolvedAt = utcNow;
    }

    /// <summary>
    /// Every method that takes the current instant takes it as a parameter, and
    /// that instant must be UTC. This is where that contract is checked rather
    /// than assumed. See <c>DECISIONS.md</c> 045, which is 039 applied here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Here rather than in <c>InProcessOrderPayments</c>.</b> The adapter gets
    /// its instant from <c>TimeProvider.GetUtcNow().UtcDateTime</c>, which cannot
    /// return anything else, so a check there would be tautological where it sits
    /// and would protect nothing from this type's other callers — the unit tests,
    /// and the reconciliation path 031 still needs. The mistake arrives at the
    /// aggregate, so the precondition belongs on the methods whose contract it is.
    /// </para>
    /// <para>
    /// <b><see cref="DateTimeKind.Unspecified"/> is refused alongside
    /// <see cref="DateTimeKind.Local"/>.</b> A wall clock with no zone is a
    /// different instant in London and in Los Angeles, so quietly reading it as
    /// UTC would be a guess wearing the costume of a conversion.
    /// </para>
    /// <para>
    /// <see cref="ArgumentException"/>, not <see cref="PaymentTransitionException"/>:
    /// a non-UTC instant is a bug in the caller, not a refusal about the state of
    /// the world, and the reason enum is a closed set.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Shared by <see cref="Retry"/> and the three reconciliation transitions, and
    /// they are the only methods that have it: the ambiguity of "no answer came
    /// back" is what licenses both reusing an idempotency key and settling an
    /// attempt on an answer this module was never told. Every other status already
    /// got its answer.
    /// </summary>
    private void GuardTimedOut()
    {
        if (Status is not PaymentStatus.TimedOut)
        {
            throw new PaymentTransitionException(Id, PaymentTransitionReason.NotTimedOut);
        }
    }
}
