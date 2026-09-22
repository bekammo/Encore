using Encore.Modules.Payments.Models;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Payments.Data;

/// <summary>
/// EF Core context owning the <c>payments</c> schema, scoped to this module.
/// </summary>
/// <remarks>
/// <para>
/// Injected concretely wherever it is needed. There is no
/// <c>IPaymentRepository</c> and there should not be: a port earns its place
/// where it buys substitution, and nothing here will ever be substituted
/// (<c>DECISIONS.md</c> 001 and 022). Nothing in this module has an interface,
/// including the gateway, and 066 kept it that way when the simulator acquired a
/// table of its own — an <c>IGatewayLedger</c> would have been the repository
/// interface 001 forbids, wearing a different name.
/// </para>
/// <para>
/// <b>One table in this context is not this module's data.</b>
/// <c>payments.gateway_ledger</c> belongs to
/// <see cref="Simulation.SimulatedPaymentGateway"/> and stands in for a third
/// party's durable store; nothing in Payments' own logic reads it. It shares this
/// context because a second context, connection string and migrator would buy a
/// distinction no test can observe. 066.
/// </para>
/// </remarks>
public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options)
    : DbContext(options)
{
    /// <summary>Every attempt to charge for an order, and how each one turned out.</summary>
    public DbSet<Payment> Payments => Set<Payment>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(PaymentsPersistence.Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PaymentsDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
