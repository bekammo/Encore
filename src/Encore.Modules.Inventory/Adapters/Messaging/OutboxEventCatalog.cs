using System.Text.Json;
using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Encore.Modules.Inventory.Adapters.Messaging;

/// <summary>
/// Maps a published event name to the code that deserialises it and hands it to
/// every registered handler. The dispatcher's whole knowledge of types lives here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> An outbox row is a name and a string; a handler is
/// <c>IIntegrationEventHandler&lt;SeatSoldV1&gt;</c>. Something has to get from one
/// to the other, and the obvious routes are both worse than this one. Reflection at
/// dispatch time — resolving a <see cref="Type"/> from the row and calling
/// <c>MakeGenericMethod</c> — pays for that on every message and fails at run time
/// when it is wrong. MediatR would do it for us and is refused outright by
/// <c>CLAUDE.md</c>, which is the right call for a repo whose argument is that you
/// pay for abstraction only where the optionality gets spent.
/// </para>
/// <para>
/// <b><see cref="Register{TEvent}"/> closes over the generic at registration.</b>
/// Each entry is an ordinary delegate by the time a message arrives, so dispatch
/// costs a dictionary lookup and a call — no reflection on the hot path, and the
/// compiler has already checked that the payload type and the handler interface
/// agree.
/// </para>
/// <para>
/// <b>An unknown name throws rather than being skipped.</b> A row whose type nobody
/// registered is an event that was written to be published and then quietly was not,
/// which is the one outcome an outbox exists to make impossible. Throwing routes it
/// into the retry-and-dead-letter path, where it is visible; skipping would mark it
/// processed and lose it.
/// </para>
/// </remarks>
internal sealed class OutboxEventCatalog
{
    private readonly Dictionary<string, Func<IServiceProvider, OutboxMessage, CancellationToken, Task>> _dispatchers =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Registers the payload type published under <paramref name="eventType"/>.
    /// </summary>
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

            // GetServices, not GetService: nothing says an event has exactly one
            // consumer, and the day a second module wants SeatSold it should be
            // able to register a handler rather than edit this module.
            var handlers = provider.GetServices<IIntegrationEventHandler<TEvent>>();

            // Sequentially and in registration order. Running them concurrently
            // would buy throughput a single-threaded dispatcher cannot use anyway,
            // and would make a failure mid-batch harder to reason about: with this
            // shape, a handler that throws means the message is retried and every
            // handler runs again, which is exactly what at-least-once already
            // requires them to tolerate.
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
