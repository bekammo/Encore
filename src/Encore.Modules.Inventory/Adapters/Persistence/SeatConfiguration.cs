using Encore.Modules.Inventory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Encore.Modules.Inventory.Adapters.Persistence;

/// <summary>
/// Maps the <see cref="Seat"/> aggregate to the <c>inventory.seats</c> table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Concurrency token: Postgres <c>xmin</c>, not an application-managed column.</b>
/// <c>xmin</c> is the system column holding the id of the transaction that last
/// wrote the row, so Postgres bumps it on every UPDATE with no cooperation from
/// us. An explicit version column would have to be incremented by something, and
/// that something is a line of code somebody can forget — on a code path whose
/// whole job is to stop two people buying the same seat, a silently skipped
/// increment is an oversell rather than a test failure. Letting the database own
/// the token also keeps <see cref="Seat"/> free of any duty to maintain its own
/// persistence metadata.
/// </para>
/// <para>
/// The cost is that this ties the aggregate's concurrency story to Postgres.
/// That is a trade I would make here: Postgres is already the declared source of
/// truth for seat state, so the coupling is to a decision that has been made
/// rather than one being foreclosed.
/// </para>
/// </remarks>
public sealed class SeatConfiguration : IEntityTypeConfiguration<Seat>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Seat> builder)
    {
        builder.ToTable("seats");

        builder.HasKey(seat => seat.Id);
        builder.Property(seat => seat.Id)
            .ValueGeneratedNever();

        builder.Property(seat => seat.EventId)
            .IsRequired();

        builder.Property(seat => seat.Status)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(seat => seat.HeldByClientId);

        builder.Property(seat => seat.HoldExpiresAt)
            .HasColumnType("timestamp with time zone");

        // xmin is a system column: mapped, never created by a migration.
        builder.Property(seat => seat.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .IsRowVersion();

        // Serves the per-client hold cap's count (DECISIONS 006), which runs on
        // every hold attempt. Column order is selectivity order: the event
        // narrows hardest, then the client, and status separates the handful of
        // rows left. HoldExpiresAt is deliberately not in the key — by the time
        // those three have been applied the candidate set is at most a few rows,
        // and keeping the index off the column that changes on every hold avoids
        // churning it on the hottest write path in the system.
        builder
            .HasIndex(seat => new { seat.EventId, seat.HeldByClientId, seat.Status })
            .HasDatabaseName("ix_seats_event_client_status");

        // Serves the expired-hold sweep's candidate query, and nothing else.
        // DECISIONS 068.
        //
        // That query asks for `Status = Held AND HoldExpiresAt <= now` ordered by
        // HoldExpiresAt, which the index above cannot answer: its leading column is
        // the event, and the sweep does not know or care which event a lapsed hold
        // belongs to. Without this it is a sequential scan plus a sort over every
        // seat in the system, once a minute, against the hottest table there is.
        //
        // Partial, filtered to held rows, for the reason ix_outbox_messages_unprocessed
        // is partial (051): the interesting set is tiny and the table is not. In a
        // healthy system almost every seat is Available or Sold, so this indexes a
        // few thousand rows whatever the seat map's size — which also keeps the
        // write cost near zero, because a seat only enters or leaves this index when
        // it is held or stops being held, never on the reads around it.
        //
        // The filter is raw SQL naming the stored int rather than the enum, because
        // that is what HasConversion<int>() above put in the column. It is checked:
        // MigrationConventionTests reads the generated migration, and an enum
        // renumbering that left this behind would change which rows are indexed
        // without changing which rows the sweep asks for.
        builder
            .HasIndex(seat => seat.HoldExpiresAt)
            .HasFilter($"\"Status\" = {(int)SeatStatus.Held}")
            .HasDatabaseName("ix_seats_expiring_holds");

        // Raised events are in-memory bookkeeping, not a column. InventoryDbContext
        // copies them into outbox_messages during SaveChanges, in the same
        // transaction as this row.
        builder.Ignore(seat => seat.DomainEvents);
    }
}
