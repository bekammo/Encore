using Encore.Modules.Orders.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Encore.Modules.Orders.Data;

/// <summary>Maps <see cref="OrderLine"/> to the <c>orders.order_lines</c> table.</summary>
/// <remarks>
/// There is deliberately no unique index on <c>SeatId</c>. It is tempting — the
/// same seat should not appear on two orders — but Inventory already guarantees
/// that, and enforcing it a second time here would be a rule with two owners
/// that can disagree. It would also be wrong at the edges: an order that failed
/// or expired still has its lines, and a seat released back to the pool and
/// bought by somebody else is a perfectly legitimate second line.
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

        // Named rather than left to EF's convention, so the migration reads the
        // same way the other indexes in this solution do.
        builder
            .HasIndex(line => line.OrderId)
            .HasDatabaseName("ix_order_lines_order");
    }
}
