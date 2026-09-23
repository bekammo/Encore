using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Encore.Modules.Catalog.Data;

/// <summary>
/// Lets <c>dotnet ef</c> construct a <see cref="CatalogDbContext"/> without
/// booting the API host. Design-time only — nothing at runtime goes through here.
/// </summary>
public sealed class CatalogDbContextFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    /// <summary>Matches the Postgres service in docker-compose.yml.</summary>
    private const string LocalDevelopmentConnection =
        "Host=localhost;Port=55432;Database=encore;Username=encore;Password=encore";

    /// <inheritdoc />
    public CatalogDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ENCORE_CATALOG_CONNECTION")
            ?? LocalDevelopmentConnection;

        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseCatalogNpgsql(connectionString)
            .Options;

        return new CatalogDbContext(options);
    }
}
