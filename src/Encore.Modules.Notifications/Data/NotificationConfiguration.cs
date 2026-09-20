using Encore.Modules.Notifications.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Encore.Modules.Notifications.Data;

/// <summary>
/// Maps <see cref="Notification"/> to the <c>notifications.notifications</c> table.
/// </summary>
/// <remarks>
/// <para>
/// <b>The unique index on <c>MessageId</c> is the load-bearing line in this file</b>,
/// for the same reason <c>ux_payments_order_live</c> is in Payments: it is the guard,
/// and the check in the handler is a courtesy. Delivery is at-least-once, so this
/// module will eventually be handed the same event twice, and two requests can both
/// pass a read-then-write check. Only the database can be the one that says no.
/// </para>
/// <para>
/// Unfiltered, unlike Payments' index. There is no notion of a notification being
/// live or spent here — a message id belongs to exactly one row for all time — so
/// there is nothing for a filter to narrow and a partial index would only be a
/// second thing to keep in step with the C#.
/// </para>
/// <para>
/// <b>No <c>xmin</c>.</b> Every other table in this repo that carries one has two
/// writers meeting on a row. A notification is inserted once and never updated, so
/// there is no second write for a token to arbitrate.
/// </para>
/// </remarks>
public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");

        builder.HasKey(notification => notification.Id);
        builder.Property(notification => notification.Id)
            .ValueGeneratedNever();

        builder.Property(notification => notification.MessageId)
            .IsRequired();

        builder.Property(notification => notification.ClientId)
            .IsRequired();

        builder.Property(notification => notification.EventId)
            .IsRequired();

        builder.Property(notification => notification.SeatId)
            .IsRequired();

        builder.Property(notification => notification.Kind)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(notification => notification.OccurredAt)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(notification => notification.CreatedAt)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // One row per delivered event, whatever the outbox does. See the remarks.
        builder
            .HasIndex(notification => notification.MessageId)
            .IsUnique()
            .HasDatabaseName("ux_notifications_message_id");

        // Serves the only question anything will ask of this table: what should
        // this client be told. Newest first is the order a reader wants, and the
        // descending key means the index answers it without a sort.
        builder
            .HasIndex(notification => new { notification.ClientId, notification.OccurredAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_notifications_client_occurred");
    }
}
