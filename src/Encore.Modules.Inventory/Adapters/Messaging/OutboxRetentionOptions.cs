namespace Encore.Modules.Inventory.Adapters.Messaging;

public sealed class OutboxRetentionOptions
{
    public const string SectionName = "Inventory:Outbox:Retention";

    public bool Enabled { get; set; } = true;

    public TimeSpan KeepDelivered { get; set; } = TimeSpan.FromDays(30);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromHours(1);

    public int BatchSize { get; set; } = 1_000;
}
