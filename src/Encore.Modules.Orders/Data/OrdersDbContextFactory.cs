using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Encore.Modules.Orders.Data;

/// <summary>
/// Lets <c>dotnet ef</c> construct an <see cref="OrdersDbContext"/> without
/// booting the API host. Design-time only — nothing at runtime goes through here.
/// </summary>
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
