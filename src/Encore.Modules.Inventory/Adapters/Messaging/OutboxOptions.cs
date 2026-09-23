namespace Encore.Modules.Inventory.Adapters.Messaging;

/// <summary>Settings for the outbox dispatcher.</summary>
public sealed class OutboxOptions
{
    public const string SectionName = "Inventory:Outbox";

    /// <summary>
    /// Whether the dispatcher runs. On by default: a dispatcher that silently did not run
    /// would stop delivery. No seat invariant may depend on it.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Wait after an empty tick. A full batch loops straight away.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Messages claimed per tick. Claimed rows stay locked until the tick commits.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// How long one handler may take before its message fails and is retried. Delivery
    /// runs inside the claim transaction, so this bounds how long locks are held.
    /// </summary>
    public TimeSpan DeliveryTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The longest one tick delivers before committing what it has. Undelivered messages
    /// are claimed again next tick.
    /// </summary>
    public TimeSpan MaxBatchDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Failures before a message becomes a dead letter: kept, but no longer retried.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>First retry delay; doubles per attempt up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);
}
