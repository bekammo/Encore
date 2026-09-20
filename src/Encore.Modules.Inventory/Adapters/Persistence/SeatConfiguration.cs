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

        // Raised events are in-memory bookkeeping, not a column. InventoryDbContext
        // copies them into outbox_messages during SaveChanges, in the same
        // transaction as this row.
        builder.Ignore(seat => seat.DomainEvents);
    }
}
