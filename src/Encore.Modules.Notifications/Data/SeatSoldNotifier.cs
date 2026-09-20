using Encore.Modules.Inventory.Contracts.Events;
using Encore.Modules.Notifications.Models;
using Encore.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Encore.Modules.Notifications.Data;

/// <summary>
/// Records that a client should be told about a seat they have bought.
/// </summary>
/// <remarks>
/// <para>
/// <b>The first consumer of anything this system publishes.</b> It reaches Inventory
/// through <c>Inventory.Contracts</c> and an event, never through a call — which is
/// the difference between this and <c>Orders</c>. Orders asks Inventory to do
/// something and waits for the answer; this is told that something happened, after
/// the fact, out of band. The seam that makes the second kind possible is the whole
/// point of the outbox.
/// </para>
/// <para>
/// <b>Idempotent by insert-and-catch rather than by read-then-write.</b> Checking for
/// an existing row first would be a check two concurrent deliveries could both pass;
/// the unique index is the real guard and this handler lets it do its job. Catching
/// the violation and returning is not swallowing an error — under at-least-once
/// delivery a redelivery is the mechanism working, so "this one is already recorded"
/// is a success, exactly as a repeat purchase of a seat you already own is (008).
/// </para>
/// <para>
/// <b>Living in <c>Data/</c> rather than a folder of its own</b> follows Catalog,
/// whose single adapter sits in <c>Data/</c> instead of getting a fourth folder built
/// to hold one file (022).
/// </para>
/// </remarks>
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
            // Already recorded by an earlier delivery of this same message. Nothing
            // to do, and nothing wrong.
            _logger.LogDebug(
                "Outbox message {MessageId} was already recorded; ignoring the redelivery.",
                messageId);

            // The failed insert is still tracked as Added, so leaving it would make
            // the next SaveChanges on this context retry it. The context is scoped
            // per dispatched batch, so this is belt and braces today and correct
            // whatever the dispatcher's scoping becomes.
            _notifications.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Whether this failure is the unique index refusing a redelivery, rather than
    /// anything else that can go wrong on the way to the database.
    /// </summary>
    /// <remarks>
    /// Matched on the constraint name, deliberately narrowly. Catching every
    /// <see cref="DbUpdateException"/> would file a dropped connection or a null in a
    /// required column under "already handled" and mark the message delivered when it
    /// was not — the same mistake 010 records the Redis adapter avoiding by
    /// translating only the two exceptions that genuinely mean "unavailable".
    /// </remarks>
    private static bool IsDuplicateMessage(DbUpdateException exception) =>
        exception.InnerException?.Message.Contains(
            "ux_notifications_message_id",
            StringComparison.Ordinal) is true;
}
