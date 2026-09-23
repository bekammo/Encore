using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Encore.Modules.Shared.Persistence;

/// <summary>
/// Shared persistence wiring: pointing a context at Postgres with its migrations history in
/// the module's own schema, and registering the module's startup migrator.
/// </summary>
public static class ModulePersistence
{
    /// <summary>
    /// EF's history table name, kept in each module's schema so a module can be extracted cleanly.
    /// </summary>
    public const string HistoryTable = "__EFMigrationsHistory";

    /// <summary>
    /// Points a context at Postgres with one module's conventions.
    /// </summary>
    public static DbContextOptionsBuilder UseModuleNpgsql(
        this DbContextOptionsBuilder builder,
        string connectionString,
        string schema) =>
        builder.UseNpgsql(
            connectionString,
            npgsql => npgsql.MigrationsHistoryTable(HistoryTable, schema));

    /// <summary>
    /// The typed overload, for callers building a <see cref="DbContextOptions{TContext}"/>.
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
    public static IServiceCollection AddModuleMigrator<TContext>(
        this IServiceCollection services,
        string moduleName)
        where TContext : DbContext =>
        services.AddHostedService(provider => new ModuleMigrator<TContext>(
            moduleName,
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ILogger<ModuleMigrator<TContext>>>()));
}
