namespace Encore.Modules.Orders.Data;

public sealed class CaptureSweepOptions
{
    public const string SectionName = "Orders:CaptureSweep";

    /// <summary>
    /// On by default: without the sweep, an order nobody confirms again would give its seats away
    /// when its authorisation lapses (025).
    /// </summary>
    public bool Enabled { get; set; } = true;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);

    public int BatchSize { get; set; } = 20;

    /// <summary>
    /// Longer than a capture takes to answer or time out, so the confirm that recorded the sale
    /// finishes first.
    /// </summary>
    public TimeSpan MinimumAge { get; set; } = TimeSpan.FromMinutes(1);
}
