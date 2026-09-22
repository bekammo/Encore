namespace Encore.Shared;

/// <summary>
/// One module's answer to "can this host actually do its job right now". A host
/// resolves every registration of this and reports them together.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists, and why it is an interface rather than a line in the host.</b>
/// Until <c>DECISIONS.md</c> 070 both hosts answered <c>/health</c> with a constant
/// <c>{ "status": "ok" }</c>, which meant compose's <c>depends_on: service_healthy</c>
/// gate — and anyone watching a deploy — was told a process was listening and
/// nothing more. The obvious fix is for the host to ping each database, and the host
/// is precisely the thing that is not allowed to know a module has a database (013,
/// 058, and the architecture tests that enforce it). So the module answers, and the
/// host counts votes.
/// </para>
/// <para>
/// <b>It lives in <c>Encore.Shared</c> for <see cref="IIntegrationEventHandler{TEvent}"/>'s
/// reason.</b> How a module reports that it is ready is an agreement every module
/// shares, and it costs nothing structurally: <c>Task</c>, <c>CancellationToken</c>
/// and a record are BCL, so <c>ENCORE001</c>–<c>003</c> still hold and
/// <c>Inventory.Domain</c> reaches no further than it did.
/// </para>
/// <para>
/// <b>A module registers one when it has something to say that nothing else says.</b>
/// Catalog, Orders and Notifications point at the same database as Inventory and run
/// no background work of their own, so a check apiece would be three more pings of
/// one server reported as three facts. If one of them ever gets its own database or
/// its own job, it gets its own check.
/// </para>
/// </remarks>
public interface IReadinessCheck
{
    /// <summary>
    /// What this check is about, as it appears in the response. Short, lower case,
    /// and stable — something a dashboard can key on.
    /// </summary>
    string Name { get; }

    /// <summary>Asks the question. Must not throw: a failure is an answer.</summary>
    Task<ReadinessResult> CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// What one <see cref="IReadinessCheck"/> found.
/// </summary>
/// <param name="Ready">
/// Whether this module can serve traffic. False takes the whole host out of the
/// ready set, so it is reserved for things that genuinely stop it working — a
/// database it cannot reach — and never for things that merely want attention.
/// </param>
/// <param name="Detail">
/// One line for a human, and the place for the numbers that want watching rather
/// than alerting on: a dead-letter count, a backlog, attempts nobody has settled.
/// <b>These deliberately do not fail the check.</b> A dead letter means one message
/// never arrived; taking the host out of rotation for it would turn a diagnostic
/// into an outage.
/// </param>
public readonly record struct ReadinessResult(bool Ready, string Detail)
{
    /// <summary>The module is ready, with something to report about how it is doing.</summary>
    public static ReadinessResult Ok(string detail) => new(true, detail);

    /// <summary>The module cannot serve traffic, and this is why.</summary>
    public static ReadinessResult Failed(string detail) => new(false, detail);
}
