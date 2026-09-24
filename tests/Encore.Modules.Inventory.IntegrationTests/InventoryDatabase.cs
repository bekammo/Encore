using Encore.Modules.Inventory.Adapters.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>
/// Rows accumulate across a class unless its tests call <see cref="ResetAsync"/>, which a class
/// does when it counts rows or reads a whole table.
/// </summary>
public sealed class InventoryDatabase : IAsyncLifetime
{
    // Every Inventory data table: add a new one here, or rows leak between tests that reset.
    private const string TruncateSql =
        $"TRUNCATE TABLE \"{InventoryPersistence.Schema}\".\"seats\", \"{InventoryPersistence.Schema}\".\"outbox_messages\" RESTART IDENTITY CASCADE";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("encore")
        .WithUsername("encore")
        .WithPassword("encore")
        .Build();

    public string ConnectionString { get; private set; } = null!;

    public DbContextOptions<InventoryDbContext> Options { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        ConnectionString = _postgres.GetConnectionString();

        Options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInventoryNpgsql(ConnectionString)
            .Options;

        await using var context = new InventoryDbContext(Options);

        // Migrate rather than EnsureCreated, so the real migrations are exercised.
        await context.Database.MigrateAsync();
    }

    public async Task ResetAsync()
    {
        await using var context = new InventoryDbContext(Options);

        await context.Database.ExecuteSqlRawAsync(TruncateSql);
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();
}
