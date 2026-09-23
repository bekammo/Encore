using System.Text.Json;
using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Encore.Modules.Inventory.Adapters.Messaging;

/// <summary>
/// Maps a published event name to code that deserialises the payload and calls every
/// registered handler. No reflection at dispatch time and no MediatR: each entry is a
/// delegate closed over its type when registered.
/// </summary>
/// <remarks>
/// An unknown name throws rather than being skipped, so the message is retried and
/// dead-lettered visibly instead of silently marked delivered.
/// </remarks>
internal sealed class OutboxEventCatalog
{
    private readonly Dictionary<string, Func<IServiceProvider, OutboxMessage, CancellationToken, Task>> _dispatchers =
        new(StringComparer.Ordinal);

    /// <summary>Registers the payload type published under <paramref name="eventType"/>.</summary>
    /// <typeparam name="TEvent">The contract record from <c>Inventory.Contracts</c>.</typeparam>
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

            // An event may have any number of consumers.
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

    /// <summary>Delivers one message to its handlers.</summary>
    /// <exception cref="NotSupportedException">Nothing is registered for the message's type.</exception>
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
