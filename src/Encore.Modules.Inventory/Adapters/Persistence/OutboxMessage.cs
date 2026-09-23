namespace Encore.Modules.Inventory.Adapters.Persistence;

/// <summary>
/// One published event, written to <c>inventory.outbox_messages</c> in the same
/// transaction as the seat change that raised it.
/// </summary>
/// <remarks>
/// <see cref="Id"/> orders rows within a transaction; <see cref="MessageId"/> is what
/// consumers deduplicate on. No concurrency token: <c>SKIP LOCKED</c> already hands each
/// row to one dispatcher.
/// </remarks>
public sealed class OutboxMessage
{
    /// <summary>For EF Core materialisation only.</summary>
    private OutboxMessage()
    {
    }

    private OutboxMessage(Guid messageId, string eventType, string payload, DateTime occurredAt)
    {
        MessageId = messageId;
        EventType = eventType;
        Payload = payload;
        OccurredAt = occurredAt;

        // Due immediately.
        NextAttemptAt = occurredAt;
    }

    /// <summary>Records an event for publication.</summary>
    /// <param name="eventType">A published name from <c>InventoryEventTypes</c>.</param>
    /// <param name="payload">The serialised contract.</param>
    /// <param name="occurredAt">When the event happened. UTC.</param>
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

        GuardUtc(occurredAt, nameof(occurredAt));

        return new OutboxMessage(messageId, eventType, payload, occurredAt);
    }

    public long Id { get; private set; }

    /// <summary>The consumer's deduplication key, stable across redeliveries.</summary>
    public Guid MessageId { get; private set; }

    /// <summary>The published name, e.g. <c>inventory.seat.sold.v1</c>.</summary>
    public string EventType { get; private set; } = string.Empty;

    public string Payload { get; private set; } = string.Empty;

    public DateTime OccurredAt { get; private set; }

    /// <summary>When delivery succeeded. Null means still to deliver.</summary>
    public DateTime? ProcessedAt { get; private set; }

    public int Attempts { get; private set; }

    /// <summary>Earliest instant this row may be claimed again.</summary>
    public DateTime NextAttemptAt { get; private set; }

    public string? LastError { get; private set; }

    public void MarkProcessed(DateTime utcNow)
    {
        GuardUtc(utcNow, nameof(utcNow));

        // LastError is kept as a record of earlier failures.
        ProcessedAt = utcNow;
    }

    /// <summary>Records a failed delivery and schedules the next attempt after <paramref name="backoff"/>.</summary>
    public void MarkFailed(DateTime utcNow, string error, TimeSpan backoff)
    {
        GuardUtc(utcNow, nameof(utcNow));

        Attempts++;
        NextAttemptAt = utcNow + backoff;
        LastError = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
    }

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
