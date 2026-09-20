using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Encore.Modules.Inventory.Adapters.Persistence;

/// <summary>
/// Maps <see cref="OutboxMessage"/> to the <c>inventory.outbox_messages</c> table.
/// </summary>
/// <remarks>
/// <para>
/// <b>The partial index is the load-bearing line in this file.</b> Processed rows
/// are kept rather than deleted, so this table only ever grows — and an unfiltered
/// index over it would grow with it, making the dispatcher's poll steadily more
/// expensive for no reason, on a schedule measured in ticks per second. Filtered to
/// <c>"ProcessedAt" IS NULL</c> it indexes only the backlog, which in a healthy
/// system is nearly empty whatever the table's size. Same mechanism as
/// <c>ux_payments_order_live</c> and <c>ux_orders_client_event_pending</c>, used
/// here for cost rather than for uniqueness.
/// </para>
/// <para>
/// <b>Keeping processed rows rather than deleting them</b> is 007's argument about
/// <c>SeatReleaseReason</c>, applied one layer out: the log is the history, and a
/// deleted row cannot answer "was this ever published, and when". Inserts do not
/// care how large the table is, and the only structure that would have cared is
/// filtered out of the problem. A retention sweep is a real future need and
/// deliberately not built now — nothing in this system currently has an opinion
/// about how long an event is worth keeping.
/// </para>
/// <para>
/// <b>The column order in the index is the claim query's order.</b>
/// <c>NextAttemptAt</c> first because it is the predicate that actually narrows —
/// most of the backlog on a busy tick is not yet due — then <c>Id</c>, which turns
/// the ORDER BY into an index read rather than a sort.
/// </para>
/// <para>
/// <b><c>jsonb</c>, not <c>text</c>.</b> <c>text</c> is cheaper to write, and this
/// is the hot path, so it is a real trade. <c>jsonb</c> wins it because a stuck row
/// is diagnosed by querying into the payload, and a payload nobody can query is a
/// blob with a timestamp next to it. It also refuses malformed JSON at the INSERT
/// rather than at the consumer.
/// </para>
/// </remarks>
public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");

        builder.HasKey(message => message.Id);

        // The one generated key in this schema. Seats carry ids their creator
        // chose; this one exists to order rows, so the database has to assign it.
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

        // The backlog, and nothing else. See the remarks above.
        builder
            .HasIndex(message => new { message.NextAttemptAt, message.Id })
            .HasFilter("\"ProcessedAt\" IS NULL")
            .HasDatabaseName("ix_outbox_messages_unprocessed");

        // Not unique, and that is not an oversight. A message id identifies an
        // event, and this table holds each one once — but uniqueness here would be
        // a constraint checked on every insert on the hottest write path in the
        // system, to guard against a bug in the drain rather than against anything
        // a user can do. The index that matters for a redelivery is the consumer's,
        // because that is where a duplicate would do damage.
        builder
            .HasIndex(message => message.MessageId)
            .HasDatabaseName("ix_outbox_messages_message_id");
    }
}
