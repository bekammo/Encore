using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Orders.Data;

/// <summary>
/// EF Core context owning the <c>orders</c> schema, scoped to this module.
/// </summary>
public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options)
    : DbContext(options)
{
    // TODO: DbSet<Order> Orders, DbSet<OrderLine> OrderLines, HasDefaultSchema("orders").
}
