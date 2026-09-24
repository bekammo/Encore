using Encore.Modules.Catalog.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Encore.Modules.Catalog.Data;

/// <summary>No foreign key to venues: the endpoint checks, so the client gets a readable answer.</summary>
public sealed class EventConfiguration : IEntityTypeConfiguration<Event>
{
    public void Configure(EntityTypeBuilder<Event> builder)
    {
        builder.ToTable("events");

        builder.HasKey(show => show.Id);
        builder.Property(show => show.Id)
            .ValueGeneratedNever();

        builder.Property(show => show.VenueId)
            .IsRequired();

        builder.Property(show => show.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(show => show.StartsAt)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(show => show.OnSaleAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(show => show.Price)
            .HasColumnType("numeric(19,4)")
            .IsRequired();

        builder.Property(show => show.Currency)
            .HasMaxLength(3)
            .IsRequired();
    }
}
