using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Encore.Modules.Shared.Persistence;

/// <summary>
/// The two things every module's persistence bootstrap had a copy of: how a
/// context is pointed at Postgres inside the module's own schema, and how that
/// module's startup migrator is registered.
/// </summary>
/// <remarks>
/// <para>
/// <b>A module keeps its own vocabulary.</b> Catalog still exposes
/// <c>UseCatalogNpgsql</c> and owns the string <c>"catalog"</c>; this only holds
/// the shape those wrappers now delegate to. That matters for more than taste —
/// the schema name is the one thing in the copied code that was never inert, and
/// leaving it where the module declares it keeps a module's schema a fact about
/// that module rather than a row in a table somewhere else.
/// </para>
/// <para>
/// <b>The history table is the reason this is worth sharing at all.</b> Three
/// callers per module must agree on it — the DI registration, the design-time
/// factory <c>dotnet ef</c> uses, and the integration tests — and a disagreement
/// is the nastiest kind available: each would read a different table to decide
/// which migrations had been applied, so the tooling and the running app would
/// hold different beliefs about the schema and neither would report an error.
/// One expression, five modules, nothing to keep in step.
/// </para>
/// </remarks>
public static class ModulePersistence
{
    /// <summary>
    /// EF's own name for the history table, kept in each module's schema rather
    /// than in <c>public</c>.
    /// </summary>
    /// <remarks>
    /// EF's default would leave a module's tables self-contained but its record of
    /// <i>which migrations had run</i> sitting in a schema shared with every other
    /// module — so extracting a module, which is the whole point of the seam, would
    /// mean unpicking one set of rows out of a shared table. DECISIONS 013.
    /// </remarks>
    public const string HistoryTable = "__EFMigrationsHistory";

    /// <summary>
    /// Points a context at Postgres with one module's conventions applied.
    /// </summary>
    public static DbContextOptionsBuilder UseModuleNpgsql(
        this DbContextOptionsBuilder builder,
        string connectionString,
        string schema) =>
        builder.UseNpgsql(
            connectionString,
            npgsql => npgsql.MigrationsHistoryTable(HistoryTable, schema));

    /// <summary>
    /// The typed overload, so callers building a
    /// <see cref="DbContextOptions{TContext}"/> — the design-time factories and the
    /// integration tests — keep their type through the call.
    /// </summary>
    public static DbContextOptionsBuilder<TContext> UseModuleNpgsql<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        string connectionString,
        string schema)
        where TContext : DbContext
    {
        ((DbContextOptionsBuilder)builder).UseModuleNpgsql(connectionString, schema);
        return builder;
    }

    /// <summary>
    /// Registers this module's startup migrator over its own context.
    /// </summary>
    /// <remarks>
    /// Registered as a hosted service exactly as the five hand-written migrators
    /// were, and resolved by the concrete type rather than by
    /// <c>AddHostedService&lt;T&gt;()</c> only because the module name has to be
    /// handed in. The host still sees an <c>IHostedService</c> and still finds an
    /// <see cref="IHostedLifecycleService"/> underneath it at run time, which is
    /// what gets the migration in before Kestrel binds.
    /// </remarks>
    public static IServiceCollection AddModuleMigrator<TContext>(
        this IServiceCollection services,
        string moduleName)
        where TContext : DbContext =>
        services.AddHostedService(provider => new ModuleMigrator<TContext>(
            moduleName,
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ILogger<ModuleMigrator<TContext>>>()));
}
