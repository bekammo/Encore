using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Orders.Data;
using Encore.Modules.Payments.Data;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Orders.IntegrationTests;

/// <summary>
/// One Postgres per test class, migrated once with every schema a class here uses: Orders' own,
/// and Inventory's and Payments' for the classes that compose those modules. A class that does
/// not use a schema pays one extra migration for it, which is cheaper than a fixture per shape.
/// Rows accumulate across the class unless its tests call <see cref="ResetAsync"/>.
/// </summary>
public sealed class OrdersDatabase : IAsyncLifetime
{
    /// <summary>Every table in the three schemas. The migrations history tables are not among them.</summary>
    private const string TruncateSql =
        $"TRUNCATE TABLE \"{OrdersPersistence.Schema}\".\"orders\", \"{OrdersPersistence.Schema}\".\"order_lines\", "
        + $"\"{InventoryPersistence.Schema}\".\"seats\", \"{InventoryPersistence.Schema}\".\"outbox_messages\", "
        + $"\"{PaymentsPersistence.Schema}\".\"payments\", \"{PaymentsPersistence.Schema}\".\"gateway_ledger\" "
        + "RESTART IDENTITY CASCADE";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("encore")
        .WithUsername("encore")
        .WithPassword("encore")
        .Build();

    /// <summary>The migrated database, for a composed container or a raw connection.</summary>
    public string ConnectionString { get; private set; } = null!;

    /// <summary>Options for an Orders context on the migrated database.</summary>
    public DbContextOptions<OrdersDbContext> Options { get; private set; } = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        ConnectionString = _postgres.GetConnectionString();

        Options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseOrdersNpgsql(ConnectionString)
            .Options;

        // Migrate rather than EnsureCreated, so the real migrations are exercised.
        await using (var orders = new OrdersDbContext(Options))
        {
            await orders.Database.MigrateAsync();
        }

        await using (var inventory = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>().UseInventoryNpgsql(ConnectionString).Options))
        {
            await inventory.Database.MigrateAsync();
        }

        await using (var payments = new PaymentsDbContext(
            new DbContextOptionsBuilder<PaymentsDbContext>().UsePaymentsNpgsql(ConnectionString).Options))
        {
            await payments.Database.MigrateAsync();
        }
    }

    /// <summary>Empties every table in the three schemas, so the next test starts from nothing.</summary>
    public async Task ResetAsync()
    {
        await using var context = new OrdersDbContext(Options);

        await context.Database.ExecuteSqlRawAsync(TruncateSql);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _postgres.DisposeAsync();
}
