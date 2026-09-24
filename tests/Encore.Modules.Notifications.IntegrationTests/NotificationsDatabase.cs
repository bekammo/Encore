using Encore.Modules.Notifications.Data;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Notifications.IntegrationTests;

/// <summary>
/// One Postgres per test class, migrated once. Nothing empties it between tests, so rows
/// accumulate across the class and every test works on fresh message ids.
/// </summary>
public sealed class NotificationsDatabase : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("encore")
        .WithUsername("encore")
        .WithPassword("encore")
        .Build();

    /// <summary>Options for a context on the migrated database.</summary>
    public DbContextOptions<NotificationsDbContext> Options { get; private set; } = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        Options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNotificationsNpgsql(_postgres.GetConnectionString())
            .Options;

        await using var context = new NotificationsDbContext(Options);

        // Migrate rather than EnsureCreated, so the real migration and its unique index are exercised.
        await context.Database.MigrateAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _postgres.DisposeAsync();
}
