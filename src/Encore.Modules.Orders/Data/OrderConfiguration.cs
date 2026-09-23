using Encore.Modules.Orders.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Encore.Modules.Orders.Data;

/// <summary>Maps <see cref="Order"/> to the <c>orders.orders</c> table.</summary>
/// <remarks>
/// The partial unique index allows one pending order per client per event; it is the real
/// guard against a duplicate checkout. Its filter is a SQL literal that depends on
/// <see cref="OrderStatus.Pending"/> being zero.
/// </remarks>
public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders");

        builder.HasKey(order => order.Id);
        builder.Property(order => order.Id)
            .ValueGeneratedNever();

        builder.Property(order => order.ClientId)
            .IsRequired();

        builder.Property(order => order.EventId)
            .IsRequired();

        builder.Property(order => order.Status)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(order => order.PlacedAt)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(order => order.HoldsExpireAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(order => order.ClosedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(order => order.Total)
            .HasColumnType("numeric(19,4)")
            .IsRequired();

        builder.Property(order => order.Currency)
            .HasMaxLength(3)
            .IsRequired();

        // xmin is a system column: mapped, never created by a migration.
        builder.Property(order => order.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .IsRowVersion();

        builder
            .HasMany(order => order.Lines)
            .WithOne()
            .HasForeignKey(line => line.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        // One open checkout per client per event.
        builder
            .HasIndex(order => new { order.ClientId, order.EventId })
            .IsUnique()
            .HasFilter("\"Status\" = 0")
            .HasDatabaseName("ux_orders_client_event_pending");
    }
}
