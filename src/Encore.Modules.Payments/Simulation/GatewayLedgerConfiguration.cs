using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Encore.Modules.Payments.Simulation;

/// <summary>
/// Maps <see cref="GatewayLedgerEntry"/> to the <c>payments.gateway_ledger</c> table.
/// </summary>
/// <remarks>
/// <para>
/// It sits beside the simulator rather than in <c>Data/</c> with
/// <c>PaymentConfiguration</c>, and the placement is the argument: this table is the
/// fake third party's, not the module's. <c>ApplyConfigurationsFromAssembly</c>
/// finds it wherever it lives, so nothing is lost by keeping the pretend gateway's
/// pieces together. <c>DECISIONS.md</c> 066.
/// </para>
/// <para>
/// <b>The key is the idempotency key itself</b>, not a surrogate. The gateway has no
/// identity for an attempt other than the one its caller presented, and making the
/// natural key the primary key is what makes a concurrent second authorisation under
/// the same key collide rather than record a second decision.
/// </para>
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

        // No xmin, and no concurrency token of any kind. A row here is written once
        // and never updated: the gateway's decision for a key does not change, which
        // is the entire property the table exists to provide. Two writers meeting on
        // one key collide on the primary key instead, and the loser reads back the
        // winner's answer.
    }
}
