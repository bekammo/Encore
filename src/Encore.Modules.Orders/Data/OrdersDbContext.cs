using Encore.Modules.Orders.Models;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Orders.Data;

/// <summary>
/// EF Core context owning the <c>orders</c> schema, scoped to this module.
/// </summary>
/// <remarks>Used directly; there is no repository interface, since nothing will substitute it.</remarks>
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
