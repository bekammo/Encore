namespace Encore.Modules.Inventory.Adapters.Messaging;

/// <summary>
/// Knobs for the outbox dispatcher.
/// </summary>
/// <remarks>
/// Every default here is a starting point rather than a measured one, and the load
/// harness is what will have an opinion about them (048).
/// </remarks>
public sealed class OutboxOptions
{
    /// <summary>Configuration section: <c>Inventory:Outbox</c>.</summary>
    public const string SectionName = "Inventory:Outbox";

    /// <summary>
    /// Whether the dispatcher runs at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>On by default, which is the opposite of <c>MigrateOnStartup</c>, and the
    /// asymmetry is deliberate.</b> A migrator that ran by default would rewrite a
    /// database as a side effect of booting, so the safe default is off and the
    /// developer opts in (013). A dispatcher that did not run by default would
    /// silently stop delivering, so the safe default is on and an operator opts
    /// out. Same question, opposite risk, opposite answer.
    /// </para>
    /// <para>
    /// Turning it off is how the claim in 007 gets tested rather than asserted: no
    /// seat invariant may depend on this running. If a test cannot pass with the
    /// dispatcher off, the dispatcher has become load-bearing and the design is
    /// broken.
    /// </para>
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long to wait after finding nothing to do.
    /// </summary>
    /// <remarks>
    /// Only ever paid on an empty tick — a tick that filled its batch goes straight
    /// round again, so a backlog drains at the speed of the database rather than at
    /// the speed of this number. That is what keeps a one-second poll from meaning
    /// one-second delivery latency under load.
    /// </remarks>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How many messages one tick claims.
    /// </summary>
    /// <remarks>
    /// Claimed rows stay locked until the tick commits, so this is really "how long
    /// a row may be held away from another dispatcher". Small keeps that window
    /// short; large amortises the round trip. Fifty is a guess that errs short.
    /// </remarks>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// How many failures before a message stops being retried.
    /// </summary>
    /// <remarks>
    /// Past this the row falls out of the claim predicate and sits there as a dead
    /// letter: still readable, no longer consuming attempts. Deliberately not
    /// deleted and deliberately not retried forever — a message that has failed five
    /// times with exponential backoff is not going to succeed on the sixth without
    /// somebody changing something.
    /// </remarks>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// The first retry delay. Doubles per attempt, capped at <see cref="MaxBackoff"/>.
    /// </summary>
    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Ceiling on the exponential backoff.</summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);
}
