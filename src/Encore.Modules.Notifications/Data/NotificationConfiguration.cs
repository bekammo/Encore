using Encore.Modules.Notifications.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Encore.Modules.Notifications.Data;

/// <summary>
/// Maps <see cref="Notification"/> to the <c>notifications.notifications</c> table.
/// </summary>
/// <remarks>
/// The unique index on <c>MessageId</c> is the real guard against a redelivery creating a
/// second row. No concurrency token: rows are inserted once and never updated.
/// </remarks>
public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    /// <summary>The unique index that makes a redelivery a no-op. The handler matches on it.</summary>
    internal const string MessageIdIndex = "ux_notifications_message_id";

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

        // One row per delivered event. Nothing reads by client yet, so nothing is indexed for it.
        builder
            .HasIndex(notification => notification.MessageId)
            .IsUnique()
            .HasDatabaseName(MessageIdIndex);
    }
}
