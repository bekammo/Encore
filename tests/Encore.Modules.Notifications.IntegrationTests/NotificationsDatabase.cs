using Encore.Modules.Notifications.Data;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Notifications.IntegrationTests;

/// <summary>Never emptied: rows accumulate across a class, so every test uses fresh message ids.</summary>
public sealed class NotificationsDatabase : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("encore")
        .WithUsername("encore")
        .WithPassword("encore")
        .Build();

    public DbContextOptions<NotificationsDbContext> Options { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        Options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNotificationsNpgsql(_postgres.GetConnectionString())
            .Options;

        await using var context = new NotificationsDbContext(Options);

        // Migrate rather than EnsureCreated, so the real migrations are exercised.
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();
}
