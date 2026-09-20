namespace Encore.Shared;

/// <summary>
/// Handles one kind of event published by another module. Implementations are
/// registered in DI and invoked by the publishing module's outbox dispatcher.
/// </summary>
/// <typeparam name="TEvent">
/// The published payload — a type from the publisher's <c>.Contracts</c> assembly,
/// never a domain type.
/// </typeparam>
/// <remarks>
/// <para>
/// <b>Why this lives in <c>Encore.Shared</c>.</b> 019 draws the line: Shared is for
/// contracts every module agrees on, not a drawer for whatever two modules happen
/// to have in common. How a module receives another module's events is exactly
/// that kind of agreement, and it sits beside <see cref="IDomainEvent"/> for the
/// same reason. It costs nothing structurally — <c>Task</c> and
/// <c>CancellationToken</c> are BCL, so <c>ENCORE001</c>–<c>003</c> stay satisfied
/// and <c>Inventory.Domain</c>, whose only project reference is this assembly,
/// reaches no further than it did before. 024's objection was about an
/// <c>IEndpointFilter</c> dragging <c>Microsoft.AspNetCore.App</c> through that
/// door; nothing like that applies here.
/// </para>
/// <para>
/// <b>Delivery is at-least-once, so every implementation must be idempotent.</b>
/// A handler can be invoked twice for one event — a process that dies after
/// handling and before the row is marked will redeliver on the next tick, and that
/// is the outbox working rather than failing. <paramref name="messageId"/> is the
/// key to deduplicate on: it is stable across redeliveries of the same event and
/// distinct between events that otherwise look identical.
/// </para>
/// <para>
/// <b>The message id is a parameter rather than a field on the payload.</b> The
/// payload describes what happened in the publisher's language; the id is a fact
/// about this delivery. Folding one into the other would put transport bookkeeping
/// into a published business contract and make the record's shape depend on how it
/// happened to be carried.
/// </para>
/// <para>
/// <b>Throwing is how a handler says "not yet".</b> The dispatcher counts the
/// attempt, records the message, and backs off. A handler that swallows its own
/// failure has told the dispatcher the event was delivered, and nothing will ever
/// retry it.
/// </para>
/// </remarks>
public interface IIntegrationEventHandler<in TEvent>
{
    /// <summary>Handles one delivery of <paramref name="integrationEvent"/>.</summary>
    /// <param name="integrationEvent">What happened, as the publisher described it.</param>
    /// <param name="messageId">
    /// Stable identity for this event, for deduplicating a redelivery.
    /// </param>
    /// <param name="cancellationToken">Cancelled when the host is shutting down.</param>
    Task HandleAsync(
        TEvent integrationEvent,
        Guid messageId,
        CancellationToken cancellationToken);
}
