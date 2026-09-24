namespace Encore.Shared;

public interface IDomainEvent
{
    /// <summary>Always UTC.</summary>
    DateTime OccurredAt { get; }
}
