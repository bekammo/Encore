using Encore.Modules.Orders.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Encore.Modules.Orders.Data;

/// <summary>Maps <see cref="OrderLine"/> to the <c>orders.order_lines</c> table.</summary>
/// <remarks>
/// No unique index on <c>SeatId</c>: Inventory already guarantees a seat sells once, and a
/// failed order's lines legitimately share seats with a later order.
/// </remarks>
public sealed class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<OrderLine> builder)
    {
        builder.ToTable("order_lines");

        builder.HasKey(line => line.Id);
        builder.Property(line => line.Id)
            .ValueGeneratedNever();

        builder.Property(line => line.OrderId)
            .IsRequired();

        builder.Property(line => line.SeatId)
            .IsRequired();

        builder.Property(line => line.UnitPrice)
            .HasColumnType("numeric(19,4)")
            .IsRequired();

        builder.Property(line => line.Currency)
            .HasMaxLength(3)
            .IsRequired();

        builder
            .HasIndex(line => line.OrderId)
            .HasDatabaseName("ix_order_lines_order");
    }
}
