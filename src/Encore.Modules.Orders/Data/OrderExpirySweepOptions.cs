namespace Encore.Modules.Orders.Data;

public sealed class OrderExpirySweepOptions
{
    public const string SectionName = "Orders:OrderExpirySweep";

    /// <summary>
    /// On by default: without the sweep, an abandoned order stays <c>Pending</c> for good, blocks
    /// the customer's next checkout for the event, and keeps its authorisation held (031).
    /// </summary>
    public bool Enabled { get; set; } = true;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);

    public int BatchSize { get; set; } = 20;

    /// <summary>
    /// How long past the recorded expiry an order waits, so a confirm that started just before
    /// the holds lapsed finishes first. Inventory, not this clock, decides each seat (006, 009).
    /// </summary>
    public TimeSpan Grace { get; set; } = TimeSpan.FromMinutes(1);
}
