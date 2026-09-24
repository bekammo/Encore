using Encore.Modules.Catalog.Contracts;
using Encore.Modules.Catalog.Data;
using Encore.Modules.Catalog.Models;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Catalog.IntegrationTests;

public sealed class InProcessEventPricingTests(CatalogDatabase database) : IClassFixture<CatalogDatabase>
{
    private static readonly DateTime StartsAt = new(2026, 7, 1, 19, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime OnSaleAt = new(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly DbContextOptions<CatalogDbContext> _options = database.Options;

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
