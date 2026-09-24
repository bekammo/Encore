namespace Encore.Modules.Inventory.Adapters.Messaging;

public sealed class OutboxOptions
{
    public const string SectionName = "Inventory:Outbox";

    public bool Enabled { get; set; } = true;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// Delivery runs inside the claim transaction, so this and <see cref="MaxBatchDuration"/>
    /// bound how long row locks are held (016).
    /// </summary>
    public TimeSpan DeliveryTimeout { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxBatchDuration { get; set; } = TimeSpan.FromSeconds(5);

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);
}
