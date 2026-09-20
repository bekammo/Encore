using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Encore.Modules.Notifications.Data;

/// <summary>
/// Builds a context for <c>dotnet ef</c> at design time.
/// </summary>
/// <remarks>
/// <para>
/// It exists so that <c>Microsoft.EntityFrameworkCore.Design</c> stays out of
/// <c>Encore.Api</c> — the host holds zero <c>PackageReference</c> items and
/// <c>ENCORE001</c> enforces it — and so this module's migrations stay
/// self-contained with it when it moves.
/// </para>
/// <para>
/// <b>Port 55432, not 5432.</b> 5432 is the likeliest port on any developer machine
/// to be occupied already, and when it is, the symptom is a
/// <c>28P01: password authentication failed</c> against a connection string that is
/// perfectly correct, because a different server answered. <c>DECISIONS.md</c> 050.
/// </para>
/// </remarks>
public sealed class NotificationsDbContextFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    private const string LocalDevelopmentConnection =
        "Host=localhost;Port=55432;Database=encore;Username=encore;Password=encore";

    /// <inheritdoc />
    public NotificationsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ENCORE_NOTIFICATIONS_CONNECTION")
            ?? LocalDevelopmentConnection;

        var options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNotificationsNpgsql(connectionString)
            .Options;

        return new NotificationsDbContext(options);
    }
}
