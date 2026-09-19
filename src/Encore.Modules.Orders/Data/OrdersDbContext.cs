using Encore.Modules.Orders.Models;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Orders.Data;

/// <summary>
/// EF Core context owning the <c>orders</c> schema, scoped to this module.
/// </summary>
/// <remarks>
/// Injected concretely wherever it is needed, including into
/// <c>CheckoutService</c>. There is no <c>IOrderRepository</c> and there should
/// not be: a port earns its place where it buys substitution, and nothing here
/// will ever be substituted (<c>DECISIONS.md</c> 001 and 022).
/// </remarks>
public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options)
    : DbContext(options)
{
    /// <summary>The orders, and the record of how each one ended.</summary>
    public DbSet<Order> Orders => Set<Order>();

    /// <summary>The seats on those orders, at the price they were bought for.</summary>
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(OrdersPersistence.Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrdersDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
