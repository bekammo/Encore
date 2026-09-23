namespace Encore.Modules.Inventory.Adapters.Messaging;

/// <summary>Settings for <see cref="OutboxRetentionSweeper"/>.</summary>
public sealed class OutboxRetentionOptions
{
    public const string SectionName = "Inventory:Outbox:Retention";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long a delivered message is kept. Only delivered rows are ever deleted;
    /// undelivered messages and dead letters are kept.
    /// </summary>
    public TimeSpan KeepDelivered { get; set; } = TimeSpan.FromDays(30);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Rows deleted per pass, so a first run is many small deletes, not one huge one.</summary>
    public int BatchSize { get; set; } = 1_000;
}
