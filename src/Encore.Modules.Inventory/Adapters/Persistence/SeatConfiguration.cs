using Encore.Modules.Inventory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Encore.Modules.Inventory.Adapters.Persistence;

/// <summary>
/// Maps <see cref="Seat"/> to <c>inventory.seats</c>. The concurrency token is Postgres's
/// <c>xmin</c>, which the database bumps on every update, so no code can forget to.
/// </summary>
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

        // For the per-client hold cap, which runs on every hold attempt.
        builder
            .HasIndex(seat => new { seat.EventId, seat.HeldByClientId, seat.Status })
            .HasDatabaseName("ix_seats_event_client_status");

        // For the expired-hold sweep. Partial, so it covers only held rows. The filter
        // names the stored int; MigrationConventionTests checks it matches SeatStatus.Held.
        builder
            .HasIndex(seat => seat.HoldExpiresAt)
            .HasFilter($"\"Status\" = {(int)SeatStatus.Held}")
            .HasDatabaseName("ix_seats_expiring_holds");

        // Events are copied into outbox_messages by InventoryDbContext, not stored here.
        builder.Ignore(seat => seat.DomainEvents);
    }
}
