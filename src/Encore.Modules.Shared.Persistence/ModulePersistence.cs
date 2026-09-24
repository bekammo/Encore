using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Encore.Modules.Shared.Persistence;

public static class ModulePersistence
{
    private const string HistoryTable = "__EFMigrationsHistory";

    /// <summary>
    /// Keeps the migrations history in the module's own schema, so extracting a module never
    /// means unpicking rows from a shared table (017). Each module wraps this as
    /// <c>Use{Module}Npgsql</c>, so DI, the design-time factory and the tests agree on both.
    /// </summary>
    public static DbContextOptionsBuilder UseModuleNpgsql(
        this DbContextOptionsBuilder builder,
        string connectionString,
        string schema) =>
        builder.UseNpgsql(
            connectionString,
            npgsql => npgsql.MigrationsHistoryTable(HistoryTable, schema));

    public static DbContextOptionsBuilder<TContext> UseModuleNpgsql<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        string connectionString,
        string schema)
        where TContext : DbContext
    {
        ((DbContextOptionsBuilder)builder).UseModuleNpgsql(connectionString, schema);
        return builder;
    }

    public static IServiceCollection AddModuleMigrator<TContext>(
        this IServiceCollection services,
        string moduleName)
        where TContext : DbContext =>
        services.AddHostedService(provider => new ModuleMigrator<TContext>(
            moduleName,
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ILogger<ModuleMigrator<TContext>>>()));
}
