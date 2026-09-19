using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Catalog.Data;

/// <summary>
/// The one place that says how a <see cref="CatalogDbContext"/> is wired to
/// Postgres. The twin of <c>InventoryPersistence</c>, and for the same reason.
/// </summary>
/// <remarks>
/// Three callers must agree — the module's DI registration, the design-time
/// factory <c>dotnet ef</c> uses, and the integration tests — and what they must
/// agree about most is the migrations history table. A disagreement there is the
/// nastiest kind available: each would read a different table to decide which
/// migrations had been applied, so the tooling and the running app would hold
/// different beliefs about the schema and neither would report an error.
/// </remarks>
public static class CatalogPersistence
{
    /// <summary>The schema this module owns. Nothing outside Catalog writes to it.</summary>
    public const string Schema = "catalog";

    /// <summary>
    /// Points a context at Postgres with this module's conventions applied.
    /// </summary>
    /// <remarks>
    /// The history table lives in the module's own schema rather than in
    /// <c>public</c>, which is EF's default. That default would leave Catalog's
    /// tables self-contained but its record of which migrations had run sitting
    /// in a schema shared with every other module (DECISIONS 013).
    /// </remarks>
    public static DbContextOptionsBuilder UseCatalogNpgsql(
        this DbContextOptionsBuilder builder,
        string connectionString) =>
        builder.UseNpgsql(
            connectionString,
            npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", Schema));

    /// <summary>
    /// The typed overload, so callers building a
    /// <see cref="DbContextOptions{TContext}"/> — the design-time factory and the
    /// integration tests — keep their type through the call.
    /// </summary>
    public static DbContextOptionsBuilder<TContext> UseCatalogNpgsql<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        string connectionString)
        where TContext : DbContext
    {
        ((DbContextOptionsBuilder)builder).UseCatalogNpgsql(connectionString);
        return builder;
    }
}
