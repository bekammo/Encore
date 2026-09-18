namespace Encore.Shared;

/// <summary>
/// Marks a fact one module publishes for other modules to consume. Unlike an
/// <see cref="IDomainEvent"/> this is a public contract: changing its shape
/// breaks someone else, so it is versioned and deliberately boring.
/// </summary>
public interface IIntegrationEvent
{
    /// <summary>Identity of this occurrence, for de-duplication downstream.</summary>
    Guid EventId { get; }

    /// <summary>When it happened, always UTC.</summary>
    DateTime OccurredAt { get; }
}
