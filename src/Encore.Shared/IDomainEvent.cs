namespace Encore.Shared;

/// <summary>
/// Marks something that happened inside a module's own model and that the rest
/// of the system may care about. Carries no dispatch mechanism of its own —
/// how (and whether) it leaves the module is the module's decision.
/// </summary>
public interface IDomainEvent
{
    /// <summary>
    /// When it happened, always UTC. <see cref="DateTime"/> rather than
    /// <see cref="DateTimeOffset"/> because every timestamp in this system is
    /// UTC and Postgres <c>timestamptz</c> discards offsets anyway — carrying an
    /// offset that is structurally always zero would be misleading ceremony.
    /// </summary>
    DateTime OccurredAt { get; }
}
