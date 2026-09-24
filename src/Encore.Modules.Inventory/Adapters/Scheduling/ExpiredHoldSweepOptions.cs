namespace Encore.Modules.Inventory.Adapters.Scheduling;

public sealed class ExpiredHoldSweepOptions
{
    public const string SectionName = "Inventory:ExpiredHoldSweep";

    public bool Enabled { get; set; } = true;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);

    public int BatchSize { get; set; } = 200;
}
