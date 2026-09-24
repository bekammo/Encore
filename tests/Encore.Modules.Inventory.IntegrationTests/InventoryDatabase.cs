using Encore.Modules.Inventory.Adapters.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Inventory.IntegrationTests;

/// <summary>
/// One Postgres per test class, migrated once. Rows accumulate across the class unless its
/// tests call <see cref="ResetAsync"/>, which a class does when it counts rows or reads a
/// whole table.
/// </summary>
public sealed class InventoryDatabase : IAsyncLifetime
{
    /// <summary>Every table Inventory owns. The migrations history table is not one of them.</summary>
    private const string TruncateSql =
        $"TRUNCATE TABLE \"{InventoryPersistence.Schema}\".\"seats\", \"{InventoryPersistence.Schema}\".\"outbox_messages\" RESTART IDENTITY CASCADE";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("encore")
        .WithUsername("encore")
        .WithPassword("encore")
        .Build();

    /// <summary>The migrated database, for a data source or a composed container.</summary>
    public string ConnectionString { get; private set; } = null!;

    /// <summary>Options for a context on the migrated database.</summary>
    public DbContextOptions<InventoryDbContext> Options { get; private set; } = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        ConnectionString = _postgres.GetConnectionString();

        Options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInventoryNpgsql(ConnectionString)
            .Options;

        await using var context = new InventoryDbContext(Options);

        // Migrate rather than EnsureCreated, so the real migration and its partial indexes are exercised.
        await context.Database.MigrateAsync();
    }

    /// <summary>Empties every Inventory table, so the next test starts with no seats and no outbox rows.</summary>
    public async Task ResetAsync()
    {
        await using var context = new InventoryDbContext(Options);

        await context.Database.ExecuteSqlRawAsync(TruncateSql);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _postgres.DisposeAsync();
}
