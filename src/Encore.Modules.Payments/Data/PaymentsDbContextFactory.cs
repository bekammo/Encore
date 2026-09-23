using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Encore.Modules.Payments.Data;

/// <summary>
/// Lets <c>dotnet ef</c> construct a <see cref="PaymentsDbContext"/> without
/// booting the API host. Design-time only — nothing at runtime goes through here.
/// </summary>
public sealed class PaymentsDbContextFactory : IDesignTimeDbContextFactory<PaymentsDbContext>
{
    /// <summary>Matches the Postgres service in docker-compose.yml.</summary>
    private const string LocalDevelopmentConnection =
        "Host=localhost;Port=55432;Database=encore;Username=encore;Password=encore";

    /// <inheritdoc />
    public PaymentsDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ENCORE_PAYMENTS_CONNECTION")
            ?? LocalDevelopmentConnection;

        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UsePaymentsNpgsql(connectionString)
            .Options;

        return new PaymentsDbContext(options);
    }
}
