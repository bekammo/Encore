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
    /// How long one handler may take before its message is failed and the queue
    /// moves on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists because delivery happens inside the claim transaction</b>, and
    /// until <c>DECISIONS.md</c> 069 nothing bounded how long that transaction could
    /// stay open. 064's fourth fault held the consumer's table under
    /// <c>ACCESS EXCLUSIVE</c> for twenty seconds and the dispatcher waited all
    /// twenty — with fifty seat-schema rows locked and a transaction open against the
    /// database the request path shares, which is how a cleanup job quietly starts
    /// holding back vacuum on the hottest table in the system.
    /// </para>
    /// <para>
    /// A handler that overruns is failed, backed off and retried like any other
    /// failure. That is the right reading: a consumer that cannot answer in two
    /// seconds is not healthy, and the outbox's promise is that late is not wrong —
    /// not that late is free.
    /// </para>
    /// <para>
    /// It is enforced on the system timer rather than <see cref="TimeProvider"/>,
    /// deliberately. This is a wall-clock guard on an external call, and a test
    /// holding a fake clock still wants its handler to be given real time.
    /// </para>
    /// </remarks>
    public TimeSpan DeliveryTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The longest one tick will keep delivering before committing what it has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other half of 069's bound, and the one that makes the first half add up:
    /// fifty messages each allowed two seconds is a hundred-second transaction,
    /// which is no better than the unbounded one it replaced. When this elapses the
    /// tick stops delivering, commits the messages it did deliver and returns; the
    /// rest were never touched, so they are simply claimed again on the next tick.
    /// </para>
    /// <para>
    /// Five seconds is a starting point, like everything else here. It bounds how
    /// long a row can be held away from a second dispatcher and how long the
    /// transaction can hold back vacuum, and it is deliberately far below the
    /// twenty-second stall 064 measured.
    /// </para>
    /// </remarks>
    public TimeSpan MaxBatchDuration { get; set; } = TimeSpan.FromSeconds(5);

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
