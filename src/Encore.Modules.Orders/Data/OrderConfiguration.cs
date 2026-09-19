using Encore.Modules.Orders.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Encore.Modules.Orders.Data;

/// <summary>Maps <see cref="Order"/> to the <c>orders.orders</c> table.</summary>
/// <remarks>
/// <para>
/// <b>The partial unique index is the load-bearing line in this file.</b> It
/// allows at most one <see cref="OrderStatus.Pending"/> order per client per
/// event, which is what makes a retried <c>POST /orders</c> safe: without it a
/// dropped response leads to a second order whose re-holds all succeed —
/// idempotently, because they are the first order's own holds — so the
/// duplicate looks perfectly valid. A check before inserting is not enough,
/// because two requests can both pass it; the database has to be the one that
/// says no.
/// </para>
/// <para>
/// <b>The filter is a SQL literal, and nothing checks it against the enum.</b>
/// <c>"Status" = 0</c> depends on <see cref="OrderStatus.Pending"/> being zero
/// and on the conversion below being to <c>int</c>. Both are pinned and
/// commented at their own definitions. A wrong filter here produces a silently
/// different index rather than an error, so the generated migration is worth
/// reading rather than trusting.
/// </para>
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

        // One open checkout per client per event. See the remarks above: this is
        // the real guard, and the check in CheckoutService is only a courtesy
        // that turns the common case into a readable refusal instead of a
        // constraint violation.
        builder
            .HasIndex(order => new { order.ClientId, order.EventId })
            .IsUnique()
            .HasFilter("\"Status\" = 0")
            .HasDatabaseName("ux_orders_client_event_pending");
    }
}
