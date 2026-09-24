using Encore.Modules.Payments.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Payments.IntegrationTests;

/// <summary>Rows accumulate across a class unless its tests call <see cref="ResetAsync"/>.</summary>
public sealed class PaymentsDatabase : IAsyncLifetime
{
    // Every Payments data table: add a new one here, or rows leak between tests that reset.
    private const string TruncateSql =
        $"TRUNCATE TABLE \"{PaymentsPersistence.Schema}\".\"payments\", \"{PaymentsPersistence.Schema}\".\"gateway_ledger\" RESTART IDENTITY CASCADE";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("encore")
        .WithUsername("encore")
        .WithPassword("encore")
        .Build();

    private ServiceProvider _provider = null!;

    public string ConnectionString { get; private set; } = null!;

    public DbContextOptions<PaymentsDbContext> Options { get; private set; } = null!;

    public IServiceScopeFactory Scopes { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        ConnectionString = _postgres.GetConnectionString();

        Options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UsePaymentsNpgsql(ConnectionString)
            .Options;

        await using (var context = new PaymentsDbContext(Options))
        {
            // Migrate rather than EnsureCreated, so the real migrations are exercised.
            await context.Database.MigrateAsync();
        }

        var services = new ServiceCollection();
        services.AddDbContext<PaymentsDbContext>(builder => builder.UsePaymentsNpgsql(ConnectionString));

        _provider = services.BuildServiceProvider();
        Scopes = _provider.GetRequiredService<IServiceScopeFactory>();
    }

    public async Task ResetAsync()
    {
        await using var context = new PaymentsDbContext(Options);

        await context.Database.ExecuteSqlRawAsync(TruncateSql);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
