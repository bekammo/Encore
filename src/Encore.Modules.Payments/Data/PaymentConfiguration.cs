using Encore.Modules.Payments.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Encore.Modules.Payments.Data;

public sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    internal const string LiveAttemptIndex = "ux_payments_order_live";

    internal static string LiveAttemptFilter =>
        $"\"Status\" IN ({string.Join(", ", Payment.LiveStatuses.Select(status => (int)status))})";

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

        // xmin is a system column: mapped, never created by a migration (004).
        builder.Property(payment => payment.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .IsRowVersion();

        builder.Ignore(payment => payment.IsLive);

        // One live attempt per order: the real guard against a double charge (013).
        builder
            .HasIndex(payment => payment.OrderId)
            .IsUnique()
            .HasFilter(LiveAttemptFilter)
            .HasDatabaseName(LiveAttemptIndex);

        builder
            .HasIndex(payment => payment.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName("ux_payments_idempotency_key");

        // One partial index per half of PaymentReconciler.Overdue.
        builder
            .HasIndex(payment => payment.ResolvedAt, "ix_payments_timed_out")
            .HasFilter($"\"Status\" = {(int)PaymentStatus.TimedOut}");

        builder
            .HasIndex(payment => payment.AttemptedAt, "ix_payments_pending")
            .HasFilter($"\"Status\" = {(int)PaymentStatus.Pending}");

        // The per-order listing: the live index's filter drops ended attempts.
        builder.HasIndex(payment => payment.OrderId, "ix_payments_order");
    }
}
