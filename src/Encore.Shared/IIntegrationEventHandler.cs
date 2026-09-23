namespace Encore.Shared;

/// <summary>
/// Handles one kind of event published by another module. Registered in DI and
/// invoked by the publishing module's outbox dispatcher.
/// </summary>
/// <typeparam name="TEvent">A payload from the publisher's <c>.Contracts</c> assembly, never a domain type.</typeparam>
/// <remarks>
/// Delivery is at-least-once, so implementations must be idempotent on
/// <c>messageId</c>. Throwing means "not yet": the dispatcher backs off and retries.
/// </remarks>
public interface IIntegrationEventHandler<in TEvent>
{
    /// <param name="integrationEvent">What happened, as the publisher described it.</param>
    /// <param name="messageId">Stable identity of the event, for deduplicating redeliveries.</param>
    /// <param name="cancellationToken">Cancelled when the host shuts down, or when the dispatcher's per-delivery deadline passes; the message is then retried.</param>
    Task HandleAsync(
        TEvent integrationEvent,
        Guid messageId,
        CancellationToken cancellationToken);
}
