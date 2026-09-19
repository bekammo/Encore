using Encore.Modules.Payments.Models;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Payments.Data;

/// <summary>
/// EF Core context owning the <c>payments</c> schema, scoped to this module.
/// </summary>
/// <remarks>
/// Injected concretely wherever it is needed. There is no
/// <c>IPaymentRepository</c> and there should not be: a port earns its place
/// where it buys substitution, and nothing here will ever be substituted
/// (<c>DECISIONS.md</c> 001 and 022). The gateway is the dependency worth an
/// interface, and it has one.
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
