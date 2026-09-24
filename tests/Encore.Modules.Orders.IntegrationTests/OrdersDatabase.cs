using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Orders.Data;
using Encore.Modules.Payments.Data;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Orders.IntegrationTests;

/// <summary>
/// Rows accumulate across a class unless its tests call <see cref="ResetAsync"/>. Every class
/// gets all three schemas, which is cheaper than a fixture per shape.
/// </summary>
public sealed class OrdersDatabase : IAsyncLifetime
{
    // Every data table in the three schemas: add a new one here, or rows leak between tests that reset.
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

    public string ConnectionString { get; private set; } = null!;

    public DbContextOptions<OrdersDbContext> Options { get; private set; } = null!;

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

    public async Task ResetAsync()
    {
        await using var context = new OrdersDbContext(Options);

        await context.Database.ExecuteSqlRawAsync(TruncateSql);
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();
}
