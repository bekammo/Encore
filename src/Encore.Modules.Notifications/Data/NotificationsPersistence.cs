using Encore.Modules.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Notifications.Data;

public static class NotificationsPersistence
{
    public const string Schema = "notifications";

    public static DbContextOptionsBuilder UseNotificationsNpgsql(
        this DbContextOptionsBuilder builder,
        string connectionString) =>
        builder.UseModuleNpgsql(connectionString, Schema);

    public static DbContextOptionsBuilder<TContext> UseNotificationsNpgsql<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        string connectionString)
        where TContext : DbContext =>
        builder.UseModuleNpgsql(connectionString, Schema);
}
