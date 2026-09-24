using Encore.Modules.Notifications.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Encore.Modules.Notifications.Data;

public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    internal const string MessageIdIndex = "ux_notifications_message_id";

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

        builder
            .HasIndex(notification => notification.MessageId)
            .IsUnique()
            .HasDatabaseName(MessageIdIndex);
    }
}
