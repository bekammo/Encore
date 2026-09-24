using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Encore.Modules.Inventory.Adapters.Persistence;

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");

        builder.HasKey(message => message.Id);

        builder.Property(message => message.Id)
            .UseIdentityAlwaysColumn();

        builder.Property(message => message.MessageId)
            .IsRequired();

        builder.Property(message => message.EventType)
            .HasMaxLength(200)
            .IsRequired();

        // jsonb, so a stuck message can be queried into.
        builder.Property(message => message.Payload)
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(message => message.OccurredAt)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(message => message.ProcessedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(message => message.Attempts)
            .IsRequired();

        builder.Property(message => message.NextAttemptAt)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(message => message.LastError)
            .HasMaxLength(OutboxMessage.MaxErrorLength);

        builder.Property(message => message.TraceParent)
            .HasMaxLength(OutboxMessage.MaxTraceParentLength);

        // Column order is the dispatcher's claim ORDER BY.
        builder
            .HasIndex(message => new { message.NextAttemptAt, message.Id })
            .HasFilter("\"ProcessedAt\" IS NULL")
            .HasDatabaseName("ix_outbox_messages_unprocessed");

        // No index on MessageId: nothing here reads by it, and each index is one more write in
        // every seat transaction. The consumer's unique index deduplicates.
    }
}
