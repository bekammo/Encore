using Encore.Modules.Orders.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Encore.Modules.Orders.Data;

/// <summary>
/// No unique index on <c>SeatId</c>: Inventory already guarantees a seat sells once, and a
/// failed order's lines legitimately share seats with a later order.
/// </summary>
public sealed class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
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
