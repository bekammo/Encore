using Encore.Modules.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Catalog.Data;

/// <summary>
/// How a <see cref="CatalogDbContext"/> is wired to Postgres. DI, the design-time factory
/// and the tests all use this, so they agree on the schema and migrations history table.
/// </summary>
public static class CatalogPersistence
{
    /// <summary>The schema this module owns. Nothing outside Catalog writes to it.</summary>
    public const string Schema = "catalog";

    /// <summary>
    /// Points a context at Postgres with this module's conventions.
    /// </summary>
    public static DbContextOptionsBuilder UseCatalogNpgsql(
        this DbContextOptionsBuilder builder,
        string connectionString) =>
        builder.UseModuleNpgsql(connectionString, Schema);

    /// <summary>
    /// The typed overload, for callers building a <see cref="DbContextOptions{TContext}"/>.
    /// </summary>
    public static DbContextOptionsBuilder<TContext> UseCatalogNpgsql<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        string connectionString)
        where TContext : DbContext =>
        builder.UseModuleNpgsql(connectionString, Schema);
}
