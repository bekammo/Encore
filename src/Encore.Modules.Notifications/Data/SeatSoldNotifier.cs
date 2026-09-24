using Encore.Modules.Inventory.Contracts.Events;
using Encore.Modules.Notifications.Models;
using Encore.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Encore.Modules.Notifications.Data;

/// <summary>
/// Sends nothing, only records. Idempotent by insert-and-catch: the unique index is the guard,
/// and a redelivery that hits it is a success, not an error (016).
/// </summary>
internal sealed class SeatSoldNotifier(
    NotificationsDbContext notifications,
    TimeProvider timeProvider,
    ILogger<SeatSoldNotifier> logger) : IIntegrationEventHandler<SeatSoldV1>
{
    private readonly NotificationsDbContext _notifications = notifications;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<SeatSoldNotifier> _logger = logger;

    /// <inheritdoc />
    public async Task HandleAsync(
        SeatSoldV1 integrationEvent,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        _notifications.Notifications.Add(new Notification
        {
            Id = Guid.CreateVersion7(),
            MessageId = messageId,
            ClientId = integrationEvent.ClientId,
            EventId = integrationEvent.EventId,
            SeatId = integrationEvent.SeatId,
            Kind = NotificationKind.SeatSold,
            OccurredAt = integrationEvent.OccurredAt,
            CreatedAt = _timeProvider.GetUtcNow().UtcDateTime
        });

        try
        {
            await _notifications.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsDuplicateMessage(ex))
        {
            _logger.LogDebug(
                "Outbox message {MessageId} was already recorded; ignoring the redelivery.",
                messageId);

            // Drop the failed insert so a later save on this context does not retry it.
            _notifications.ChangeTracker.Clear();
        }
    }

    // Matched on SQL state and constraint name, so no other failure passes for "already handled".
    private static bool IsDuplicateMessage(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
        && postgres.ConstraintName == NotificationConfiguration.MessageIdIndex;
}
