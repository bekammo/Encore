using Encore.Modules.Inventory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Encore.Modules.Inventory.Adapters.Persistence;

public sealed class SeatConfiguration : IEntityTypeConfiguration<Seat>
{
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

        // xmin settles races (004). A system column: mapped, never created by a migration.
        builder.Property(seat => seat.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .IsRowVersion();

        builder
            .HasIndex(seat => new { seat.EventId, seat.HeldByClientId, seat.Status })
            .HasDatabaseName("ix_seats_event_client_status");

        // The filter pins SeatStatus.Held's stored int; MigrationConventionTests checks the
        // migration still matches.
        builder
            .HasIndex(seat => seat.HoldExpiresAt)
            .HasFilter($"\"Status\" = {(int)SeatStatus.Held}")
            .HasDatabaseName("ix_seats_expiring_holds");

        builder.Ignore(seat => seat.DomainEvents);
    }
}
