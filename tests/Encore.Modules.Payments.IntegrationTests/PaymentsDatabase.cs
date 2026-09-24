using Encore.Modules.Payments.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Payments.IntegrationTests;

/// <summary>
/// One Postgres per test class, migrated once, since a container per test would start dozens.
/// Rows accumulate across the class unless its tests call <see cref="ResetAsync"/>, which a
/// class does when it sweeps every attempt or needs a gateway that has answered nothing.
/// </summary>
public sealed class PaymentsDatabase : IAsyncLifetime
{
    /// <summary>Every table Payments owns. The migrations history table is not one of them.</summary>
    private const string TruncateSql =
        $"TRUNCATE TABLE \"{PaymentsPersistence.Schema}\".\"payments\", \"{PaymentsPersistence.Schema}\".\"gateway_ledger\" RESTART IDENTITY CASCADE";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("encore")
        .WithUsername("encore")
        .WithPassword("encore")
        .Build();

    private ServiceProvider _provider = null!;

    /// <summary>The migrated database, for a host or a composed container.</summary>
    public string ConnectionString { get; private set; } = null!;

    /// <summary>Options for a context on the migrated database.</summary>
    public DbContextOptions<PaymentsDbContext> Options { get; private set; } = null!;

    /// <summary>What the gateway resolves its context through.</summary>
    public IServiceScopeFactory Scopes { get; private set; } = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        ConnectionString = _postgres.GetConnectionString();

        Options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UsePaymentsNpgsql(ConnectionString)
            .Options;

        await using (var context = new PaymentsDbContext(Options))
        {
            // Migrate rather than EnsureCreated, so the real migration is exercised.
            await context.Database.MigrateAsync();
        }

        var services = new ServiceCollection();
        services.AddDbContext<PaymentsDbContext>(builder => builder.UsePaymentsNpgsql(ConnectionString));

        _provider = services.BuildServiceProvider();
        Scopes = _provider.GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>
    /// Empties the attempts and the gateway's ledger, so the next test starts with no payments
    /// and a gateway that has answered nothing.
    /// </summary>
    public async Task ResetAsync()
    {
        await using var context = new PaymentsDbContext(Options);

        await context.Database.ExecuteSqlRawAsync(TruncateSql);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
