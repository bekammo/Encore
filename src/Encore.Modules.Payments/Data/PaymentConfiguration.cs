using Encore.Modules.Payments.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Encore.Modules.Payments.Data;

/// <summary>Maps <see cref="Payment"/> to the <c>payments.payments</c> table.</summary>
/// <remarks>
/// The partial unique index allows one live attempt per order: the real guard against a
/// double charge. Its filter lists <see cref="Payment.LiveStatuses"/> as a SQL literal, kept
/// in step by <c>PaymentTests.IsLive_ShouldMatchTheIndexFilter</c>.
/// </remarks>
public sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("payments");

        builder.HasKey(payment => payment.Id);
        builder.Property(payment => payment.Id)
            .ValueGeneratedNever();

        builder.Property(payment => payment.OrderId)
            .IsRequired();

        builder.Property(payment => payment.ClientId)
            .IsRequired();

        builder.Property(payment => payment.Amount)
            .HasColumnType("numeric(19,4)")
            .IsRequired();

        builder.Property(payment => payment.Currency)
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(payment => payment.Status)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(payment => payment.IdempotencyKey)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(payment => payment.GatewayReference)
            .HasMaxLength(100);

        builder.Property(payment => payment.AttemptedAt)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(payment => payment.ResolvedAt)
            .HasColumnType("timestamp with time zone");

        // xmin is a system column: mapped, never created by a migration.
        builder.Property(payment => payment.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .IsRowVersion();

        // Computed from Status; no column.
        builder.Ignore(payment => payment.IsLive);

        // One live attempt per order.
        builder
            .HasIndex(payment => payment.OrderId)
            .IsUnique()
            .HasFilter("\"Status\" IN (0, 1, 2, 4)")
            .HasDatabaseName("ux_payments_order_live");

        // A key is never reused across rows, only within one.
        builder
            .HasIndex(payment => payment.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName("ux_payments_idempotency_key");
    }
}
