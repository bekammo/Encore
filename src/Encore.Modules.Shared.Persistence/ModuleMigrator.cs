using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Encore.Modules.Shared.Persistence;

/// <summary>
/// Migrates in <see cref="StartingAsync"/>, which the host calls before any <c>StartAsync</c>,
/// so the schema exists before Kestrel accepts a request.
/// </summary>
public sealed class ModuleMigrator<TContext>(
    string moduleName,
    IServiceScopeFactory scopeFactory,
    ILogger<ModuleMigrator<TContext>> logger) : IHostedLifecycleService
    where TContext : DbContext
{
    private readonly string _moduleName = moduleName;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<ModuleMigrator<TContext>> _logger = logger;

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
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

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
