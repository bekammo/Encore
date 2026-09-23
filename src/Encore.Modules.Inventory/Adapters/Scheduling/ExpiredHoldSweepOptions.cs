namespace Encore.Modules.Inventory.Adapters.Scheduling;

/// <summary>Settings for <see cref="ExpiredHoldSweeper"/>.</summary>
public sealed class ExpiredHoldSweepOptions
{
    public const string SectionName = "Inventory:ExpiredHoldSweep";

    /// <summary>
    /// Whether the sweep runs. Turning it off must not break any invariant;
    /// <c>ExpiryWithoutTheSweepTests</c> checks that.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Wait after a sweep that did not fill its batch. Nothing waits on this job.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Seats visited per sweep.</summary>
    public int BatchSize { get; set; } = 200;
}
