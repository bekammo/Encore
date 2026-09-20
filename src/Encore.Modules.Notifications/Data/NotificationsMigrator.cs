using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Encore.Modules.Notifications.Data;

/// <summary>
/// Applies Notifications' outstanding migrations while the host is starting, so a
/// developer who has just run <c>docker compose up</c> gets a working schema
/// from <c>dotnet run</c> without a separate step.
/// </summary>
/// <remarks>
/// <para>
/// <b>Development convenience, never the production path.</b> Registered only
/// when <c>Notifications:MigrateOnStartup</c> is set, which it is in the run
/// profiles and nowhere else. The real deployment story stays
/// <c>dotnet ef database update</c>, or a generated script — schema changes are
/// a deliberate act, and an app that quietly rewrites the database as a side
/// effect of booting is a bad thing to have in production even when it works.
/// </para>
/// <para>
/// <b>Why <see cref="IHostedLifecycleService"/> and not <c>IHostedService</c>.</b>
/// Hosted services start in registration order, and the web host's own service
/// is registered before any module's, so an <c>IHostedService.StartAsync</c>
/// here would run *after* Kestrel had begun accepting requests — leaving a
/// window where a request could hit a table that does not exist yet. The host
/// calls <see cref="StartingAsync"/> on every lifecycle service before it calls
/// <c>StartAsync</c> on any of them, so migrating there is strictly before the
/// socket opens.
/// </para>
/// <para>
/// Concurrent instances are safe: the Npgsql provider takes a lock for the
/// duration of a migration, so a second instance waits rather than racing.
/// </para>
/// </remarks>
public sealed class NotificationsMigrator(
    IServiceScopeFactory scopeFactory,
    ILogger<NotificationsMigrator> logger) : IHostedLifecycleService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<NotificationsMigrator> _logger = logger;

    /// <inheritdoc />
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        // The context is scoped; a hosted service is a singleton.
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        var pending = (await context.Database
            .GetPendingMigrationsAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToList();

        if (pending.Count is 0)
        {
            _logger.LogInformation("Notifications schema is up to date.");
            return;
        }

        _logger.LogInformation(
            "Applying {Count} Notifications migration(s): {Migrations}.",
            pending.Count,
            string.Join(", ", pending));

        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Notifications schema updated.");
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
