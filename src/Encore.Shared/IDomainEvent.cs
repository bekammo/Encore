namespace Encore.Shared;

/// <summary>
/// Something that happened inside a module's model. How it leaves the module is the
/// module's decision.
/// </summary>
public interface IDomainEvent
{
    /// <summary>When it happened, always UTC.</summary>
    DateTime OccurredAt { get; }
}
