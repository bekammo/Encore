using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Encore.Modules.Inventory.Adapters.Persistence;

/// <summary>
/// Maps <see cref="OutboxMessage"/> to <c>inventory.outbox_messages</c>. The payload is
/// <c>jsonb</c> so a stuck message can be queried into.
/// </summary>
public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");

        builder.HasKey(message => message.Id);

        // Database-assigned, because it exists to order rows.
        builder.Property(message => message.Id)
            .UseIdentityAlwaysColumn();

        builder.Property(message => message.MessageId)
            .IsRequired();

        builder.Property(message => message.EventType)
            .HasMaxLength(200)
            .IsRequired();

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

        // Nullable: a seat changed outside a traced operation has no trace to link to.
        builder.Property(message => message.TraceParent)
            .HasMaxLength(OutboxMessage.MaxTraceParentLength);

        // Partial: indexes only the backlog, so the dispatcher's poll stays cheap as the
        // table grows. Column order matches the claim query.
        builder
            .HasIndex(message => new { message.NextAttemptAt, message.Id })
            .HasFilter("\"ProcessedAt\" IS NULL")
            .HasDatabaseName("ix_outbox_messages_unprocessed");

        // Not unique: that would cost a check on every insert on the hot path. The
        // consumer's index is the one that must be unique.
        builder
            .HasIndex(message => message.MessageId)
            .HasDatabaseName("ix_outbox_messages_message_id");
    }
}
