using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Encore.Modules.Payments.Simulation;

/// <summary>
/// Maps <see cref="GatewayLedgerEntry"/> to the <c>payments.gateway_ledger</c> table.
/// </summary>
/// <remarks>
/// The primary key is the idempotency key itself, so a concurrent second decision under the
/// same key collides instead of being recorded.
/// </remarks>
internal sealed class GatewayLedgerConfiguration : IEntityTypeConfiguration<GatewayLedgerEntry>
{
    /// <summary>The primary key's name, which the gateway matches on to spot a lost race.</summary>
    internal const string PrimaryKeyName = "pk_gateway_ledger";

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<GatewayLedgerEntry> builder)
    {
        builder.ToTable("gateway_ledger");

        builder.HasKey(entry => entry.IdempotencyKey)
            .HasName(PrimaryKeyName);

        builder.Property(entry => entry.IdempotencyKey)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(entry => entry.Outcome)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(entry => entry.RecordedAt)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // No concurrency token: rows are written once and never updated.
    }
}
