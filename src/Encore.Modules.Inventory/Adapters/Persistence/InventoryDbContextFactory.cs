using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Encore.Modules.Inventory.Adapters.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> construct an <see cref="InventoryDbContext"/> without
/// booting the API host. Design-time only — nothing at runtime goes through here.
/// </summary>
public sealed class InventoryDbContextFactory : IDesignTimeDbContextFactory<InventoryDbContext>
{
    /// <summary>Matches the Postgres service in docker-compose.yml.</summary>
    private const string LocalDevelopmentConnection =
        "Host=localhost;Port=55432;Database=encore;Username=encore;Password=encore";

    /// <inheritdoc />
    public InventoryDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ENCORE_INVENTORY_CONNECTION")
            ?? LocalDevelopmentConnection;

        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInventoryNpgsql(connectionString)
            .Options;

        return new InventoryDbContext(options);
    }
}
