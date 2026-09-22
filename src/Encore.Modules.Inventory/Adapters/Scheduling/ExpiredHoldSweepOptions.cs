namespace Encore.Modules.Inventory.Adapters.Scheduling;

/// <summary>
/// Knobs for <see cref="ExpiredHoldSweeper"/>.
/// </summary>
/// <remarks>
/// Every default here is a starting point rather than a measured one — the same
/// admission <c>OutboxOptions</c> and <c>PaymentReconciliationOptions</c> make.
/// The difference is that nothing is waiting on any of these numbers: a row this
/// job has not reached yet is already logically available to every reader
/// (<c>DECISIONS.md</c> 007), so the cost of setting them badly is untidy rows
/// rather than a late answer to anybody.
/// </remarks>
public sealed class ExpiredHoldSweepOptions
{
    /// <summary>Configuration section: <c>Inventory:ExpiredHoldSweep</c>.</summary>
    public const string SectionName = "Inventory:ExpiredHoldSweep";

    /// <summary>
    /// Whether the sweep runs at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On by default, for the reason <c>OutboxOptions.Enabled</c> gives rather than
    /// <c>MigrateOnStartup</c>'s: a cleanup job that did not run by default would
    /// silently leave the table accumulating stale rows.
    /// </para>
    /// <para>
    /// <b>Turning it off is the whole falsifiability story.</b> 007 says that if a
    /// test cannot pass with the sweep disabled, the sweep has become load-bearing
    /// and the design is broken. Every Inventory test in the tree predates this
    /// class and passes without it, and
    /// <c>ExpiryWithoutTheSweepTests</c> makes the claim directly rather than
    /// leaving it to be inferred from that history. The k6 rig's
    /// <c>SWEEP_ENABLED</c> asks the same question under load.
    /// </para>
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long to wait after a sweep that did not fill its batch.
    /// </summary>
    /// <remarks>
    /// <b>A minute, which is the reconciler's scale rather than the dispatcher's
    /// second, and the gap is deliberate.</b> An undelivered event is late news
    /// somebody is waiting for. An unswept row is not news at all: every read and
    /// write path already treats it as available, so the only thing a faster sweep
    /// buys is a tidier table. Polling the hot database more often than that to
    /// buy tidiness would repeat exactly what 056 measured the dispatcher doing to
    /// the request path.
    /// </remarks>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How many seats one sweep visits.
    /// </summary>
    /// <remarks>
    /// Each one costs a load and a conditional write of its own, so this is the
    /// ceiling on how much database the job asks for in one go. A full batch means
    /// there is more waiting and the loop goes straight round again, so this bounds
    /// the burst rather than the rate.
    /// </remarks>
    public int BatchSize { get; set; } = 200;
}
