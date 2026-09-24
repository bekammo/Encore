namespace Encore.Shared;

/// <summary>
/// Delivery is at least once, in no order a handler may rely on (024), so be idempotent on
/// <c>messageId</c>. A throw, or the token cancelled at the per-delivery deadline (016), means
/// "not yet": the dispatcher backs off and retries.
/// </summary>
/// <typeparam name="TEvent">A payload from the publisher's <c>.Contracts</c> assembly, never a domain type.</typeparam>
public interface IIntegrationEventHandler<in TEvent>
{
    Task HandleAsync(
        TEvent integrationEvent,
        Guid messageId,
        CancellationToken cancellationToken);
}
