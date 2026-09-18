using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Payments.Data;

/// <summary>
/// EF Core context owning the <c>payments</c> schema, scoped to this module.
/// </summary>
public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options)
    : DbContext(options)
{
    // TODO: DbSet<Payment> Payments, HasDefaultSchema("payments").
}
