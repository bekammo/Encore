using Encore.Modules.Payments.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Encore.Modules.Payments.Data;

/// <summary>Maps <see cref="Payment"/> to the <c>payments.payments</c> table.</summary>
/// <remarks>
/// The partial unique index allows one live attempt per order: the real guard against a
/// double charge. Its filter is built from <see cref="Payment.LiveStatuses"/>, so changing that
/// list changes the model, and <c>MigrateAsync</c> refuses to run until a migration moves the
/// index with it.
/// </remarks>
public sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    /// <summary>
    /// The live-attempt index, the real guard against a double charge. Refusals are matched on it.
    /// </summary>
    internal const string LiveAttemptIndex = "ux_payments_order_live";

    /// <summary>The live-attempt index's filter: <c>"Status" IN (0, 1, 2, 4)</c> today.</summary>
    internal static string LiveAttemptFilter =>
        $"\"Status\" IN ({string.Join(", ", Payment.LiveStatuses.Select(status => (int)status))})";

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
            .HasFilter(LiveAttemptFilter)
            .HasDatabaseName(LiveAttemptIndex);

        // A key is never reused across rows, only within one.
        builder
            .HasIndex(payment => payment.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName("ux_payments_idempotency_key");

        // What the reconciler and the readiness check read: the few attempts still unanswered,
        // one partial index per half of PaymentReconciler.Overdue.
        builder
            .HasIndex(payment => payment.ResolvedAt, "ix_payments_timed_out")
            .HasFilter($"\"Status\" = {(int)PaymentStatus.TimedOut}");

        builder
            .HasIndex(payment => payment.AttemptedAt, "ix_payments_pending")
            .HasFilter($"\"Status\" = {(int)PaymentStatus.Pending}");

        // A customer's attempts for one order, in every status. The live index above cannot
        // serve it: its filter leaves the ended attempts out.
        builder.HasIndex(payment => payment.OrderId, "ix_payments_order");
    }
}
