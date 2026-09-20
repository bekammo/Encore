using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Encore.Modules.Orders.Data;

/// <summary>
/// Lets <c>dotnet ef</c> construct an <see cref="OrdersDbContext"/> without
/// booting the API host. Design-time only — nothing at runtime goes through here.
/// </summary>
/// <remarks>
/// The alternative was to reference <c>Microsoft.EntityFrameworkCore.Design</c>
/// from <c>Encore.Api</c>, which is what the EF tooling assumes by default. This
/// way keeps the host at zero package references and keeps each module's
/// migrations self-contained, so they travel with the module.
/// </remarks>
public sealed class OrdersDbContextFactory : IDesignTimeDbContextFactory<OrdersDbContext>
{
    /// <summary>Matches the Postgres service in docker-compose.yml.</summary>
    private const string LocalDevelopmentConnection =
        "Host=localhost;Port=55432;Database=encore;Username=encore;Password=encore";

    /// <inheritdoc />
    public OrdersDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ENCORE_ORDERS_CONNECTION")
            ?? LocalDevelopmentConnection;

        var options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseOrdersNpgsql(connectionString)
            .Options;

        return new OrdersDbContext(options);
    }
}
