using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Inventory.Adapters.Persistence;

/// <summary>
/// The one place that says how an <see cref="InventoryDbContext"/> is wired to
/// Postgres.
/// </summary>
/// <remarks>
/// There are three callers — the module's DI registration, the design-time
/// factory that <c>dotnet ef</c> uses, and the integration tests — and they must
/// agree. They agree on the migrations history table in particular, because a
/// disagreement there is the nastiest kind: each would read a different table to
/// decide which migrations had been applied, so the tooling and the running app
/// would hold different beliefs about the schema and neither would report an
/// error.
/// </remarks>
public static class InventoryPersistence
{
    /// <summary>The schema this module owns. Nothing outside Inventory writes to it.</summary>
    public const string Schema = "inventory";

    /// <summary>
    /// Points a context at Postgres with this module's conventions applied.
    /// </summary>
    /// <remarks>
    /// The history table lives in the module's own schema rather than in
    /// <c>public</c>, which is where EF would put it by default. That default
    /// would leave Inventory's tables self-contained but its record of *which
    /// migrations had run* sitting in a schema shared with every other module —
    /// so extracting Inventory to its own service, which is the whole point of
    /// the module seam, would mean unpicking one table out of a shared one.
    /// </remarks>
    public static DbContextOptionsBuilder UseInventoryNpgsql(
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
    public static DbContextOptionsBuilder<TContext> UseInventoryNpgsql<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        string connectionString)
        where TContext : DbContext
    {
        ((DbContextOptionsBuilder)builder).UseInventoryNpgsql(connectionString);
        return builder;
    }
}
