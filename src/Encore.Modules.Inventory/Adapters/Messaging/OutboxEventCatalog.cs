using System.Text.Json;
using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Encore.Modules.Inventory.Adapters.Messaging;

/// <summary>
/// No reflection at dispatch time: each entry is a delegate closed over its type at
/// registration, so the compiler checks that payload and handler agree (016). An unknown name
/// throws rather than being skipped, so the message dead-letters visibly.
/// </summary>
internal sealed class OutboxEventCatalog
{
    private readonly Dictionary<string, Func<IServiceProvider, OutboxMessage, CancellationToken, Task>> _dispatchers =
        new(StringComparer.Ordinal);

    internal OutboxEventCatalog Register<TEvent>(string eventType)
    {
        _dispatchers[eventType] = static async (provider, message, cancellationToken) =>
        {
            var contract = JsonSerializer.Deserialize<TEvent>(
                message.Payload,
                SeatEventPublication.SerializerOptions);

            if (contract is null)
            {
                throw new InvalidOperationException(
                    $"Outbox message {message.MessageId} ('{message.EventType}') deserialised to null.");
            }

            // Any number of consumers, including none: an event nobody handles is marked delivered (024).
            var handlers = provider.GetServices<IIntegrationEventHandler<TEvent>>();

            // Sequential: if one throws, the message is retried and every handler runs again.
            foreach (var handler in handlers)
            {
                await handler.HandleAsync(contract, message.MessageId, cancellationToken)
                    .ConfigureAwait(false);
            }
        };

        return this;
    }

    internal Task DispatchAsync(
        IServiceProvider provider,
        OutboxMessage message,
        CancellationToken cancellationToken) =>
        _dispatchers.TryGetValue(message.EventType, out var dispatch)
            ? dispatch(provider, message, cancellationToken)
            : throw new NotSupportedException(
                $"No payload type registered for outbox event type '{message.EventType}'. "
                + $"Register it in {nameof(OutboxEventCatalog)} where the module is composed.");
}
