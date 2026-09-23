using Encore.Modules.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Orders.Data;

/// <summary>
/// How an <see cref="OrdersDbContext"/> is wired to Postgres. DI, the design-time factory
/// and the tests all use this, so they agree on the schema and migrations history table.
/// </summary>
public static class OrdersPersistence
{
    /// <summary>The schema this module owns. Nothing outside Orders writes to it.</summary>
    public const string Schema = "orders";

    /// <summary>
    /// Points a context at Postgres with this module's conventions.
    /// </summary>
    public static DbContextOptionsBuilder UseOrdersNpgsql(
        this DbContextOptionsBuilder builder,
        string connectionString) =>
        builder.UseModuleNpgsql(connectionString, Schema);

    /// <summary>
    /// The typed overload, for callers building a <see cref="DbContextOptions{TContext}"/>.
    /// </summary>
    public static DbContextOptionsBuilder<TContext> UseOrdersNpgsql<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        string connectionString)
        where TContext : DbContext =>
        builder.UseModuleNpgsql(connectionString, Schema);
}
