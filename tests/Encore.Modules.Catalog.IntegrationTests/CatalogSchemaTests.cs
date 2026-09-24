using Encore.Modules.Catalog.Data;
using Encore.Modules.Catalog.Models;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Catalog.IntegrationTests;

public sealed class CatalogSchemaTests(CatalogDatabase database) : IClassFixture<CatalogDatabase>
{
    private readonly DbContextOptions<CatalogDbContext> _options = database.Options;

    [Fact]
    public async Task Migrate_ShouldLeaveNoPendingMigrations()
    {
        await using var context = new CatalogDbContext(_options);

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task Price_ShouldRoundTripWithoutLosingPrecision()
    {
        var eventId = Guid.NewGuid();

        await using (var write = new CatalogDbContext(_options))
        {
            write.Events.Add(NewEvent(eventId, price: 123.4567m));
            await write.SaveChangesAsync();
        }

        await using var read = new CatalogDbContext(_options);
        var stored = await read.Events.SingleAsync(show => show.Id == eventId);

        Assert.Equal(123.4567m, stored.Price);
        Assert.Equal("GBP", stored.Currency);
    }

    [Fact]
    public async Task Timestamps_ShouldRoundTripAsUtc()
    {
        var eventId = Guid.NewGuid();
        var startsAt = new DateTime(2026, 7, 1, 19, 30, 0, DateTimeKind.Utc);
        var onSaleAt = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);

        await using (var write = new CatalogDbContext(_options))
        {
            var show = NewEvent(eventId, price: 50m);
            show.StartsAt = startsAt;
            show.OnSaleAt = onSaleAt;

            write.Events.Add(show);
            await write.SaveChangesAsync();
        }

        await using var read = new CatalogDbContext(_options);
        var stored = await read.Events.SingleAsync(show => show.Id == eventId);

        Assert.Equal(startsAt, stored.StartsAt);
        Assert.Equal(DateTimeKind.Utc, stored.StartsAt.Kind);
        Assert.Equal(onSaleAt, stored.OnSaleAt);
        Assert.Equal(DateTimeKind.Utc, stored.OnSaleAt!.Value.Kind);
    }

    [Fact]
    public async Task OnSaleAt_ShouldBeNullable()
    {
        var eventId = Guid.NewGuid();

        await using (var write = new CatalogDbContext(_options))
        {
            var show = NewEvent(eventId, price: 50m);
            show.OnSaleAt = null;

            write.Events.Add(show);
            await write.SaveChangesAsync();
        }

        await using var read = new CatalogDbContext(_options);

        Assert.Null((await read.Events.SingleAsync(show => show.Id == eventId)).OnSaleAt);
    }

    [Fact]
    public async Task Venue_ShouldRoundTrip()
    {
        var venueId = Guid.NewGuid();

        await using (var write = new CatalogDbContext(_options))
        {
            write.Venues.Add(new Venue
            {
                Id = venueId,
                Name = "Royal Albert Hall",
                Address = "Kensington Gore, London SW7 2AP",
                Capacity = 5272
            });

            await write.SaveChangesAsync();
        }

        await using var read = new CatalogDbContext(_options);
        var stored = await read.Venues.SingleAsync(venue => venue.Id == venueId);

        Assert.Equal("Royal Albert Hall", stored.Name);
        Assert.Equal(5272, stored.Capacity);
    }

    private static Event NewEvent(Guid id, decimal price) => new()
    {
        Id = id,
        VenueId = Guid.NewGuid(),
        Name = "A show",
        StartsAt = new DateTime(2026, 7, 1, 19, 30, 0, DateTimeKind.Utc),
        OnSaleAt = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc),
        Price = price,
        Currency = "GBP"
    };
}
