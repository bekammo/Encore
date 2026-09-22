using Encore.Modules.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Catalog.Data;

/// <summary>
/// The one place that says how a <see cref="CatalogDbContext"/> is wired to
/// Postgres. The twin of <c>InventoryPersistence</c>, and for the same reason.
/// </summary>
/// <remarks>
/// <para>
/// Three callers must agree — the module's DI registration, the design-time
/// factory <c>dotnet ef</c> uses, and the integration tests — and what they must
/// agree about most is the migrations history table. A disagreement there is the
/// nastiest kind available: each would read a different table to decide which
/// migrations had been applied, so the tooling and the running app would hold
/// different beliefs about the schema and neither would report an error.
/// </para>
/// <para>
/// Since DECISIONS 058 the <i>shape</i> of that wiring lives in
/// <see cref="ModulePersistence"/> and this type keeps the two things that are
/// Catalog's own: the schema name and the vocabulary its callers use. The schema
/// is the one thing in the old copied code that was never inert, which is why it
/// stays declared here rather than becoming a row in a table somewhere else.
/// </para>
/// </remarks>
public static class CatalogPersistence
{
    /// <summary>The schema this module owns. Nothing outside Catalog writes to it.</summary>
    public const string Schema = "catalog";

    /// <summary>
    /// Points a context at Postgres with this module's conventions applied.
    /// </summary>
    public static DbContextOptionsBuilder UseCatalogNpgsql(
        this DbContextOptionsBuilder builder,
        string connectionString) =>
        builder.UseModuleNpgsql(connectionString, Schema);

    /// <summary>
    /// The typed overload, so callers building a
    /// <see cref="DbContextOptions{TContext}"/> — the design-time factory and the
    /// integration tests — keep their type through the call.
    /// </summary>
    public static DbContextOptionsBuilder<TContext> UseCatalogNpgsql<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        string connectionString)
        where TContext : DbContext =>
        builder.UseModuleNpgsql(connectionString, Schema);
}
