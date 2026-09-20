namespace Encore.Modules.Inventory.Adapters.Persistence;

/// <summary>
/// One published event, written into <c>inventory.outbox_messages</c> in the same
/// transaction as the seat change that raised it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A persistence type, and deliberately not in <c>Inventory.Domain</c>.</b> The
/// domain raises events and has never heard of an outbox; how they leave the module
/// is an adapter's business, exactly as <c>xmin</c> is. Putting this next to
/// <see cref="InventoryDbContext"/> keeps the rule that 002 makes the compiler
/// enforce from depending on anybody's restraint.
/// </para>
/// <para>
/// <b>Two identities, because they answer different questions.</b>
/// <see cref="Id"/> is a database sequence and exists to order rows;
/// <see cref="MessageId"/> travels with the payload and exists so a consumer can
/// recognise a redelivery. They are kept apart because <see cref="Id"/> is not safe
/// to publish: it is assigned at INSERT while transactions commit in a different
/// order, so it orders a transaction's own rows correctly and says nothing
/// dependable across transactions. That is the ordering guarantee this design
/// actually makes, and it is the one 007 needs — a reclaim raises
/// <c>SeatReleased</c> then <c>SeatHeld</c> inside one save, so the pair is always
/// adjacent and always in that order.
/// </para>
/// <para>
/// <b>No <c>xmin</c>.</b> Every other table in this repo carries a concurrency
/// token because two writers can meet on a row. Here they cannot: the dispatcher
/// claims rows with <c>FOR UPDATE SKIP LOCKED</c>, so a row is handed to exactly
/// one reader and a second reader is given a different one. A token would guard a
/// race that the claim has already made unreachable.
/// </para>
/// <para>
/// Constructed by factory and with no public setters, per 005 — <c>Attempts</c> and
/// <c>ProcessedAt</c> are the dispatcher's bookkeeping, and a row that is both
/// processed and pending a retry is a state nothing should be able to reach.
/// </para>
/// </remarks>
public sealed class OutboxMessage
{
    /// <summary>For EF Core's materialisation pipeline only.</summary>
    private OutboxMessage()
    {
    }

    private OutboxMessage(Guid messageId, string eventType, string payload, DateTime occurredAt)
    {
        MessageId = messageId;
        EventType = eventType;
        Payload = payload;
        OccurredAt = occurredAt;

        // Due immediately. A fresh row has no reason to wait, and a nullable
        // column would mean every claim query carried an OR.
        NextAttemptAt = occurredAt;
    }

    /// <summary>
    /// Records an event for publication.
    /// </summary>
    /// <param name="messageId">Stable identity carried to the consumer.</param>
    /// <param name="eventType">A published name from <c>InventoryEventTypes</c>.</param>
    /// <param name="payload">The serialised contract.</param>
    /// <param name="occurredAt">When the event happened, from the domain event. UTC.</param>
    public static OutboxMessage For(
        Guid messageId,
        string eventType,
        string payload,
        DateTime occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        if (messageId == Guid.Empty)
        {
            throw new ArgumentException("A message id is required.", nameof(messageId));
        }

        // Postgres timestamptz stores UTC and Npgsql rejects anything else, so a
        // Local instant would fail several layers from whoever produced it. The
        // domain already promises UTC; this is where that promise is checked
        // rather than assumed, for 045's reason.
        GuardUtc(occurredAt, nameof(occurredAt));

        return new OutboxMessage(messageId, eventType, payload, occurredAt);
    }

    /// <summary>Sequence position. Orders the rows written by one transaction.</summary>
    public long Id { get; private set; }

    /// <summary>The consumer's deduplication key. Stable across redeliveries.</summary>
    public Guid MessageId { get; private set; }

    /// <summary>The published name, e.g. <c>inventory.seat.sold.v1</c>.</summary>
    public string EventType { get; private set; } = string.Empty;

    /// <summary>The serialised contract.</summary>
    public string Payload { get; private set; } = string.Empty;

    /// <summary>When the event happened, copied from the domain event. UTC.</summary>
    public DateTime OccurredAt { get; private set; }

    /// <summary>When every handler accepted it. Null means still to deliver.</summary>
    public DateTime? ProcessedAt { get; private set; }

    /// <summary>How many deliveries have been tried and failed.</summary>
    public int Attempts { get; private set; }

    /// <summary>The earliest instant this row may be claimed again. UTC.</summary>
    public DateTime NextAttemptAt { get; private set; }

    /// <summary>What went wrong last time, if anything did.</summary>
    public string? LastError { get; private set; }

    /// <summary>Records a successful delivery.</summary>
    public void MarkProcessed(DateTime utcNow)
    {
        GuardUtc(utcNow, nameof(utcNow));

        ProcessedAt = utcNow;

        // LastError is deliberately kept. A row that failed twice and then
        // succeeded is more useful to read than one that has tidied away the
        // evidence, and ProcessedAt already says how the story ended.
    }

    /// <summary>
    /// Records a failed delivery and schedules the next attempt.
    /// </summary>
    /// <remarks>
    /// The backoff is passed in rather than computed here: how long to wait is the
    /// dispatcher's policy, and this row only stores the answer.
    /// </remarks>
    public void MarkFailed(DateTime utcNow, string error, TimeSpan backoff)
    {
        GuardUtc(utcNow, nameof(utcNow));

        Attempts++;
        NextAttemptAt = utcNow + backoff;

        // Bounded, because an exception with a long chain of inner messages would
        // otherwise be free to grow this row without limit on a path that runs on
        // every retry.
        LastError = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
    }

    /// <summary>As much of a failure message as is worth keeping.</summary>
    internal const int MaxErrorLength = 1_000;

    private static void GuardUtc(DateTime value, string parameterName)
    {
        if (value.Kind is not DateTimeKind.Utc)
        {
            throw new ArgumentException(
                $"The instant must be UTC, but its Kind was {value.Kind}.",
                parameterName);
        }
    }
}
