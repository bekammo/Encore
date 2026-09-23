namespace Encore.Modules.Orders.Data;

/// <summary>Settings for <see cref="CaptureSweeper"/>.</summary>
public sealed class CaptureSweepOptions
{
    public const string SectionName = "Orders:CaptureSweep";

    /// <summary>
    /// Whether the sweep runs. On by default: an order nobody confirms again would otherwise
    /// give its seats away when the authorisation lapses.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Wait between sweeps. Each order in a batch costs a capture round trip.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Orders per sweep.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>
    /// How long an order must have been owed its capture before this touches it. Longer than a
    /// capture takes to answer or time out, so the confirm that recorded the sale finishes first.
    /// </summary>
    public TimeSpan MinimumAge { get; set; } = TimeSpan.FromMinutes(1);
}
