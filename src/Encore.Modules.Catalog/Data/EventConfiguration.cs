using Encore.Modules.Catalog.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Encore.Modules.Catalog.Data;

/// <summary>Maps <see cref="Event"/> to the <c>catalog.events</c> table.</summary>
/// <remarks>
/// Money is <c>numeric(19,4)</c>: never a float, never Postgres <c>money</c>. No foreign key to
/// venues; the endpoint checks the venue exists, and the index serves the query.
/// </remarks>
public sealed class EventConfiguration : IEntityTypeConfiguration<Event>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Event> builder)
    {
        builder.ToTable("events");

        builder.HasKey(@event => @event.Id);
        builder.Property(@event => @event.Id)
            .ValueGeneratedNever();

        builder.Property(@event => @event.VenueId)
            .IsRequired();

        builder.Property(@event => @event.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(@event => @event.StartsAt)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(@event => @event.OnSaleAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(@event => @event.Price)
            .HasColumnType("numeric(19,4)")
            .IsRequired();

        builder.Property(@event => @event.Currency)
            .HasMaxLength(3)
            .IsRequired();

        // "What is on at this venue."
        builder
            .HasIndex(@event => @event.VenueId)
            .HasDatabaseName("ix_events_venue");
    }
}
