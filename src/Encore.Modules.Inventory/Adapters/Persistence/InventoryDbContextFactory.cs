using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Encore.Modules.Inventory.Adapters.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> construct an <see cref="InventoryDbContext"/> without
/// booting the API host. Design-time only — nothing at runtime goes through here.
/// </summary>
/// <remarks>
/// The alternative was to reference <c>Microsoft.EntityFrameworkCore.Design</c>
/// from <c>Encore.Api</c>, which is what the EF tooling assumes by default. This
/// way is better for a modular monolith aiming at extraction: the host keeps zero
/// package references and stays a pure composition root, and Inventory's
/// migrations are self-contained, so they travel with the module the day it
/// becomes its own service.
/// </remarks>
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
