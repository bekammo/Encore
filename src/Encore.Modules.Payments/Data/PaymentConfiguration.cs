using Encore.Modules.Payments.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Encore.Modules.Payments.Data;

/// <summary>Maps <see cref="Payment"/> to the <c>payments.payments</c> table.</summary>
/// <remarks>
/// <para>
/// <b>The partial unique index is the load-bearing line in this file.</b> It
/// allows at most one live attempt per order, which is what makes a retried
/// confirm safe: without it a dropped response leads to a second authorisation
/// against the same order, and the customer is charged twice for one set of
/// seats. A check before inserting is not enough, because two requests can both
/// pass it; the database has to be the one that says no. The reasoning is
/// <c>DECISIONS.md</c> 030.
/// </para>
/// <para>
/// <b>The filter is a SQL literal, and nothing checks it against the enum.</b>
/// <c>"Status" IN (0, 1, 2, 4)</c> is <see cref="Payment.IsLive"/> written out by
/// hand, because a computed property cannot be an index filter. The two are pinned
/// to each other by <c>PaymentTests.IsLive_ShouldMatchTheIndexFilter</c> and by
/// nothing else — a wrong filter here produces a silently different index rather
/// than an error, so the generated migration is worth reading rather than
/// trusting.
/// </para>
/// <para>
/// <b>Declined and Voided are deliberately outside the filter.</b> Both moved no
/// money and never will, so neither should stop a customer trying again. Timed out
/// is inside it, because "the gateway never answered" honestly reads as "possibly
/// holding funds" — see 031.
/// </para>
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

        // xmin is a system column: mapped, never created by a migration. Here to
        // stop a confirm and a cancel racing this row from writing Voided over
        // money that was taken — see the remarks on Payment.RowVersion.
        builder.Property(payment => payment.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .IsRowVersion();

        // Computed from Status, so it has no column of its own. The index below
        // repeats its definition in SQL because a filter cannot call into C#.
        builder.Ignore(payment => payment.IsLive);

        // One live attempt per order. See the remarks above: this is the real
        // guard against a double charge, and the read in the adapter is only a
        // courtesy that turns the common case into a readable answer instead of a
        // constraint violation.
        builder
            .HasIndex(payment => payment.OrderId)
            .IsUnique()
            .HasFilter("\"Status\" IN (0, 1, 2, 4)")
            .HasDatabaseName("ux_payments_order_live");

        // A gateway that recognises a repeat needs the key to be unique to the
        // attempt. Unfiltered: a key is never reused across rows, only within one.
        builder
            .HasIndex(payment => payment.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName("ux_payments_idempotency_key");
    }
}
