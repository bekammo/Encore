namespace Encore.Modules.Inventory.Adapters.Persistence;

/// <summary>No concurrency token: <c>SKIP LOCKED</c> already hands each row to one dispatcher.</summary>
public sealed class OutboxMessage
{
    internal const int MaxErrorLength = 1_000;

    /// <summary>version-traceid-spanid-flags: 2 + 32 + 16 + 2 characters and three dashes.</summary>
    internal const int MaxTraceParentLength = 55;

    // EF Core materialisation only.
    private OutboxMessage()
    {
    }

    private OutboxMessage(Guid messageId, string eventType, string payload, DateTime occurredAt, string? traceParent)
    {
        MessageId = messageId;
        EventType = eventType;
        Payload = payload;
        OccurredAt = occurredAt;
        TraceParent = traceParent;

        NextAttemptAt = occurredAt;
    }

    public static OutboxMessage For(
        Guid messageId,
        string eventType,
        string payload,
        DateTime occurredAt,
        string? traceParent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        if (messageId == Guid.Empty)
        {
            throw new ArgumentException("A message id is required.", nameof(messageId));
        }

        GuardUtc(occurredAt, nameof(occurredAt));

        if (traceParent is { Length: > MaxTraceParentLength })
        {
            throw new ArgumentException(
                $"A traceparent is at most {MaxTraceParentLength} characters.",
                nameof(traceParent));
        }

        return new OutboxMessage(messageId, eventType, payload, occurredAt, traceParent);
    }

    public long Id { get; private set; }

    /// <summary>The consumer's deduplication key, stable across redeliveries.</summary>
    public Guid MessageId { get; private set; }

    public string EventType { get; private set; } = string.Empty;

    public string Payload { get; private set; } = string.Empty;

    public DateTime OccurredAt { get; private set; }

    /// <summary>Null means undelivered: still due, or a dead letter once Attempts reaches MaxAttempts.</summary>
    public DateTime? ProcessedAt { get; private set; }

    public int Attempts { get; private set; }

    public DateTime NextAttemptAt { get; private set; }

    public string? LastError { get; private set; }

    public string? TraceParent { get; private set; }

    public void MarkProcessed(DateTime utcNow)
    {
        GuardUtc(utcNow, nameof(utcNow));

        // LastError is kept as a record of earlier failures.
        ProcessedAt = utcNow;
    }

    public void MarkFailed(DateTime utcNow, string error, TimeSpan backoff)
    {
        GuardUtc(utcNow, nameof(utcNow));

        Attempts++;
        NextAttemptAt = utcNow + backoff;
        LastError = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
    }

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
