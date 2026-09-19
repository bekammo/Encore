using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Encore.Modules.Payments.Data;

/// <summary>
/// Applies Payments' outstanding migrations while the host is starting, so a
/// developer who has just run <c>docker compose up</c> gets a working schema from
/// <c>dotnet run</c> without a separate step.
/// </summary>
/// <remarks>
/// <para>
/// Development convenience, never the production path — registered only when
/// <c>Payments:MigrateOnStartup</c> is set, which the run profiles do and nothing
/// else does. The reasoning is DECISIONS 013's in full, including why this is an
/// <see cref="IHostedLifecycleService"/> doing its work in
/// <see cref="StartingAsync"/>: lifecycle services all run <c>StartingAsync</c>
/// before any service's <c>StartAsync</c>, so this is strictly before Kestrel
/// opens the socket, and a request cannot arrive at a table that does not exist.
/// </para>
/// <para>
/// One migrator per module rather than a single host-level step (DECISIONS 017).
/// A shared step would have to name every module's context, which puts EF Core
/// into <c>Encore.Api</c> and costs it the zero-package property it keeps on
/// purpose. The duplication is the price of the seam.
/// </para>
/// </remarks>
public sealed class PaymentsMigrator(
    IServiceScopeFactory scopeFactory,
    ILogger<PaymentsMigrator> logger) : IHostedLifecycleService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<PaymentsMigrator> _logger = logger;

    /// <inheritdoc />
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        // The context is scoped; a hosted service is a singleton.
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        var pending = (await context.Database
            .GetPendingMigrationsAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToList();

        if (pending.Count is 0)
        {
            _logger.LogInformation("Payments schema is up to date.");
            return;
        }

        _logger.LogInformation(
            "Applying {Count} Payments migration(s): {Migrations}.",
            pending.Count,
            string.Join(", ", pending));

        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Payments schema updated.");
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
