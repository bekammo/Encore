using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Encore.Modules.Payments.Simulation;

internal sealed class GatewayLedgerConfiguration : IEntityTypeConfiguration<GatewayLedgerEntry>
{
    internal const string PrimaryKeyName = "pk_gateway_ledger";

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
    }
}
