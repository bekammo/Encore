using Encore.Modules.Payments.Models;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Payments.Data;

/// <summary>
/// EF Core context for the <c>payments</c> schema, used directly with no repository interface.
/// </summary>
/// <remarks>
/// <c>payments.gateway_ledger</c> belongs to the simulated gateway, not to Payments; it shares
/// this context only to avoid a second context and migrator.
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
