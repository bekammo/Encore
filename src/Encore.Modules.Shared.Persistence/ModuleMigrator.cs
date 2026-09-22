using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Encore.Modules.Shared.Persistence;

/// <summary>
/// Applies one module's outstanding migrations while the host is starting, so a
/// developer who has just run <c>docker compose up</c> gets a working schema from
/// <c>dotnet run</c> without a separate step.
/// </summary>
/// <typeparam name="TContext">The module's context. The type parameter is the
/// whole of what used to differ between five hand-written copies.</typeparam>
/// <remarks>
/// <para>
/// <b>Development convenience, never the production path.</b> A module registers
/// this only when its own <c>{Module}:MigrateOnStartup</c> setting is true, which
/// the run profiles set and nothing else does. The real deployment story stays
/// <c>dotnet ef database update</c>, or a generated script — schema changes are a
/// deliberate act, and an app that quietly rewrites the database as a side effect
/// of booting is a bad thing to have in production even when it works.
/// </para>
/// <para>
/// <b>Why <see cref="IHostedLifecycleService"/> and not <c>IHostedService</c>.</b>
/// Hosted services start in registration order, and the web host's own service is
/// registered before any module's, so an <c>IHostedService.StartAsync</c> here
/// would run <i>after</i> Kestrel had begun accepting requests — leaving a window
/// where a request could hit a table that does not exist yet. The host calls
/// <see cref="StartingAsync"/> on every lifecycle service before it calls
/// <c>StartAsync</c> on any of them, so migrating there is strictly before the
/// socket opens.
/// </para>
/// <para>
/// Concurrent instances are safe: the Npgsql provider takes a lock for the
/// duration of a migration, so a second instance waits rather than racing.
/// </para>
/// <para>
/// <b>Still one migrator per module.</b> Sharing an implementation is not the same
/// as sharing a step — 017 refused a host-level migrator and that refusal stands.
/// Each module registers its own instance, over its own context, behind its own
/// flag, and carries it away when it is extracted. What changed in 058 is only
/// that the five instances stopped being five files.
/// </para>
/// </remarks>
public sealed class ModuleMigrator<TContext>(
    string moduleName,
    IServiceScopeFactory scopeFactory,
    ILogger<ModuleMigrator<TContext>> logger) : IHostedLifecycleService
    where TContext : DbContext
{
    private readonly string _moduleName = moduleName;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<ModuleMigrator<TContext>> _logger = logger;

    /// <inheritdoc />
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        // The context is scoped; a hosted service is a singleton.
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();

        var pending = (await context.Database
            .GetPendingMigrationsAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToList();

        if (pending.Count is 0)
        {
            _logger.LogInformation("{Module} schema is up to date.", _moduleName);
            return;
        }

        _logger.LogInformation(
            "Applying {Count} {Module} migration(s): {Migrations}.",
            pending.Count,
            _moduleName,
            string.Join(", ", pending));

        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("{Module} schema updated.", _moduleName);
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
