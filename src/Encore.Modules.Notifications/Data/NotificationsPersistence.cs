using Encore.Modules.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Notifications.Data;

/// <summary>
/// How a <see cref="NotificationsDbContext"/> is wired to Postgres. DI, the design-time
/// factory and the tests all use this, so they agree on the schema and migrations history table.
/// </summary>
public static class NotificationsPersistence
{
    /// <summary>The schema this module owns. Nothing outside Notifications writes to it.</summary>
    public const string Schema = "notifications";

    /// <summary>
    /// Points a context at Postgres with this module's conventions.
    /// </summary>
    public static DbContextOptionsBuilder UseNotificationsNpgsql(
        this DbContextOptionsBuilder builder,
        string connectionString) =>
        builder.UseModuleNpgsql(connectionString, Schema);

    /// <summary>
    /// The typed overload, for callers building a <see cref="DbContextOptions{TContext}"/>.
    /// </summary>
    public static DbContextOptionsBuilder<TContext> UseNotificationsNpgsql<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        string connectionString)
        where TContext : DbContext =>
        builder.UseModuleNpgsql(connectionString, Schema);
}
