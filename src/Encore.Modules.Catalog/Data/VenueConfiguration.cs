using Encore.Modules.Catalog.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Encore.Modules.Catalog.Data;

/// <summary>Maps <see cref="Venue"/> to the <c>catalog.venues</c> table.</summary>
public sealed class VenueConfiguration : IEntityTypeConfiguration<Venue>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Venue> builder)
    {
        builder.ToTable("venues");

        builder.HasKey(venue => venue.Id);
        builder.Property(venue => venue.Id)
            .ValueGeneratedNever();

        builder.Property(venue => venue.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(venue => venue.Address)
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(venue => venue.Capacity)
            .IsRequired();
    }
}
