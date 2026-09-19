using Encore.Modules.Catalog.Contracts;
using Encore.Modules.Catalog.Data;
using Encore.Modules.Catalog.Models;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Catalog.IntegrationTests;

/// <summary>
/// The adapter behind <see cref="IEventPricing"/>, against real Postgres.
/// </summary>
/// <remarks>
/// This is the contract Orders will take a dependency on, so what is worth
/// proving is that the promises on the interface actually hold: a missing event
/// is an ordinary answer rather than an exception, a found one carries every
/// field a caller needs, and the sale window survives the round trip as UTC.
/// </remarks>
public sealed class InProcessEventPricingTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("encore")
        .WithUsername("encore")
        .WithPassword("encore")
        .Build();

    private static readonly DateTime StartsAt = new(2026, 7, 1, 19, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime OnSaleAt = new(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);

    private DbContextOptions<CatalogDbContext> _options = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        _options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseCatalogNpgsql(_postgres.GetConnectionString())
            .Options;

        await using var context = new CatalogDbContext(_options);
        await context.Database.MigrateAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task Get_WhenEventExists_ShouldReturnItsPriceAndSaleWindow()
    {
        var eventId = await GivenAnEventAsync(price: 42.5000m, onSaleAt: OnSaleAt);

        await using var context = new CatalogDbContext(_options);
        var pricing = new InProcessEventPricing(context);

        var response = await pricing.GetAsync(new EventPricingRequest(eventId));

        Assert.Equal(EventPricingStatus.Priced, response.Status);
        Assert.Equal(42.5000m, response.UnitPrice);
        Assert.Equal("GBP", response.Currency);
        Assert.Equal(OnSaleAt, response.OnSaleAt);
        Assert.Equal(StartsAt, response.StartsAt);
    }

    /// <summary>
    /// A missing event is an ordinary answer, not an exception. Orders decides
    /// whether to start a checkout by switching on this status, and branching on
    /// exception types to do it would be exactly the shape DECISIONS 008 argues
    /// against above the aggregate.
    /// </summary>
    [Fact]
    public async Task Get_WhenEventMissing_ShouldReportNotFoundRatherThanThrow()
    {
        await using var context = new CatalogDbContext(_options);
        var pricing = new InProcessEventPricing(context);

        var response = await pricing.GetAsync(new EventPricingRequest(Guid.NewGuid()));

        Assert.Equal(EventPricingStatus.EventNotFound, response.Status);
        Assert.Null(response.UnitPrice);
        Assert.Null(response.Currency);
    }

    /// <summary>
    /// Null means on sale immediately, and the contract has to be able to carry
    /// that distinction or the gate in Orders cannot read it.
    /// </summary>
    [Fact]
    public async Task Get_WhenNoOnSaleDate_ShouldReportNull()
    {
        var eventId = await GivenAnEventAsync(price: 10m, onSaleAt: null);

        await using var context = new CatalogDbContext(_options);
        var pricing = new InProcessEventPricing(context);

        var response = await pricing.GetAsync(new EventPricingRequest(eventId));

        Assert.Equal(EventPricingStatus.Priced, response.Status);
        Assert.Null(response.OnSaleAt);
    }

    [Fact]
    public async Task Get_WhenRequestIsNull_ShouldThrow()
    {
        await using var context = new CatalogDbContext(_options);
        var pricing = new InProcessEventPricing(context);

        await Assert.ThrowsAsync<ArgumentNullException>(() => pricing.GetAsync(null!));
    }

    private async Task<Guid> GivenAnEventAsync(decimal price, DateTime? onSaleAt)
    {
        var eventId = Guid.NewGuid();

        await using var context = new CatalogDbContext(_options);

        context.Events.Add(new Event
        {
            Id = eventId,
            VenueId = Guid.NewGuid(),
            Name = "A show",
            StartsAt = StartsAt,
            OnSaleAt = onSaleAt,
            Price = price,
            Currency = "GBP"
        });

        await context.SaveChangesAsync();

        return eventId;
    }
}
