namespace Encore.Modules.Inventory.Adapters.Messaging;

/// <summary>
/// Knobs for <see cref="OutboxRetentionSweeper"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>051 refused to build this, and the refusal was right at the time.</b> Its
/// argument was that nothing here had an opinion about how long an event is worth
/// keeping, and that inventing ninety days would be a guess wearing a policy's
/// clothing. What 069 changes is the other side of it: "forever" is also a policy,
/// and it is one nobody chose either. <c>DECISIONS.md</c> 070 supersedes 051 on this
/// point alone; everything else 051 says about the table stands, including why
/// processed rows are marked rather than deleted the moment they are sent.
/// </para>
/// <para>
/// The number below is a starting point and is written down as one. What it is not
/// is a silent default: a retention window somebody can see and argue with is a
/// different object from an unbounded table nobody has looked at.
/// </para>
/// </remarks>
public sealed class OutboxRetentionOptions
{
    /// <summary>Configuration section: <c>Inventory:Outbox:Retention</c>.</summary>
    public const string SectionName = "Inventory:Outbox:Retention";

    /// <summary>
    /// Whether delivered rows are ever deleted.
    /// </summary>
    /// <remarks>
    /// On by default, for <c>ExpiredHoldSweepOptions.Enabled</c>'s reason: a cleanup
    /// job that did not run by default would leave the table growing while looking
    /// like it was being looked after. Off is a supported answer — an operator who
    /// wants the full event history kept sets this to false and says so out loud.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long a delivered message is kept after it was delivered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Thirty days, chosen rather than found: long enough that the question 051
    /// cared about — what share of holds lapse rather than convert — is answerable
    /// over any period anybody has actually asked about, and short enough that the
    /// table's size is a function of throughput rather than of uptime.
    /// </para>
    /// <para>
    /// <b>Only delivered rows are in scope.</b> An undelivered message is work, and a
    /// dead letter is evidence; neither is deleted by age or by anything else here.
    /// A dead letter that nobody ever looks at stays in the table forever, which is
    /// the correct outcome for a message that was never sent.
    /// </para>
    /// </remarks>
    public TimeSpan KeepDelivered { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How long to wait between passes.</summary>
    /// <remarks>
    /// An hour. Nothing waits on this — the rows it removes were delivered weeks ago
    /// — and polling the hot database more often to tidy it would repeat what 056
    /// measured the dispatcher doing to the request path.
    /// </remarks>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How many rows one pass deletes.
    /// </summary>
    /// <remarks>
    /// Bounded so that a first run against a table nobody has ever pruned is a long
    /// series of small deletes rather than one enormous one holding a transaction
    /// open — which is exactly the failure 069 has just finished bounding on the
    /// dispatcher. A full batch means more is waiting, so the loop goes straight
    /// round again.
    /// </remarks>
    public int BatchSize { get; set; } = 1_000;
}
