using Encore.Modules.Catalog.Data;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Catalog.IntegrationTests;

/// <summary>Never emptied: rows accumulate across a class, so every test uses fresh ids.</summary>
public sealed class CatalogDatabase : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("encore")
        .WithUsername("encore")
        .WithPassword("encore")
        .Build();

    public DbContextOptions<CatalogDbContext> Options { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        Options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseCatalogNpgsql(_postgres.GetConnectionString())
            .Options;

        await using var context = new CatalogDbContext(Options);

        // Migrate rather than EnsureCreated, so the real migrations are exercised.
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();
}
