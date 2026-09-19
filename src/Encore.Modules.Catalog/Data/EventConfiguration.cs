using Encore.Modules.Catalog.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Encore.Modules.Catalog.Data;

/// <summary>Maps <see cref="Event"/> to the <c>catalog.events</c> table.</summary>
/// <remarks>
/// <para>
/// <b>Money is <c>numeric(19,4)</c>.</b> Never a floating-point type, which
/// cannot represent most decimal fractions exactly and would quietly turn a
/// summed order total into a number nobody agreed to. Never the Postgres
/// <c>money</c> type either: its scale is a database-wide setting and its
/// rendering is locale-dependent, so the same column means different things on
/// two servers. Four decimal places rather than two leaves room for prices that
/// are not whole cents without inviting the question again later.
/// </para>
/// <para>
/// There is deliberately no foreign key to <c>venues</c>. The endpoint checks
/// the venue exists before inserting, and a constraint would add a second
/// enforcement point for a rule that has exactly one writer — the same reason
/// Inventory's seats carry a bare <c>EventId</c> and no constraint back to
/// Catalog. What the index below buys is the query, not the integrity.
/// </para>
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

        // Serves "what is on at this venue", the only cross-row question this
        // module is asked today.
        builder
            .HasIndex(@event => @event.VenueId)
            .HasDatabaseName("ix_events_venue");
    }
}
