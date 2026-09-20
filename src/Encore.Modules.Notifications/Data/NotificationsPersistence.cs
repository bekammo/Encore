using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Notifications.Data;

/// <summary>
/// The one place that says how a <see cref="NotificationsDbContext"/> is wired to
/// Postgres.
/// </summary>
/// <remarks>
/// Three callers must agree — the module's DI registration, the design-time
/// factory <c>dotnet ef</c> uses, and the integration tests — and they agree on
/// the migrations history table in particular, because a disagreement there is the
/// nastiest kind: each would read a different table to decide which migrations had
/// been applied, and neither would report an error.
/// </remarks>
public static class NotificationsPersistence
{
    /// <summary>The schema this module owns. Nothing outside Notifications writes to it.</summary>
    public const string Schema = "notifications";

    /// <summary>Points a context at Postgres with this module's conventions applied.</summary>
    public static DbContextOptionsBuilder UseNotificationsNpgsql(
        this DbContextOptionsBuilder builder,
        string connectionString) =>
        builder.UseNpgsql(
            connectionString,
            npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", Schema));

    /// <summary>The typed overload, so callers keep their type through the call.</summary>
    public static DbContextOptionsBuilder<TContext> UseNotificationsNpgsql<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        string connectionString)
        where TContext : DbContext
    {
        ((DbContextOptionsBuilder)builder).UseNotificationsNpgsql(connectionString);
        return builder;
    }
}
