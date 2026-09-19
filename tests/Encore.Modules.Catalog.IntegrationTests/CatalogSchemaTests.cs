using Encore.Modules.Catalog.Data;
using Encore.Modules.Catalog.Models;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Catalog.IntegrationTests;

/// <summary>
/// Proves Catalog's mapping means what it says against real Postgres.
/// </summary>
/// <remarks>
/// There is no behaviour in this module to test — it is CRUD over tables nobody
/// contends for (DECISIONS 001) — so these tests aim at the two places a
/// read-mostly catalogue can still be quietly wrong: money that loses precision
/// on the way to the database, and a timestamp that comes back as a different
/// instant or a different <see cref="DateTimeKind"/> than it went in as.
/// </remarks>
public sealed class CatalogSchemaTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("encore")
        .WithUsername("encore")
        .WithPassword("encore")
        .Build();

    private DbContextOptions<CatalogDbContext> _options = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        _options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseCatalogNpgsql(_postgres.GetConnectionString())
            .Options;

        await using var context = new CatalogDbContext(_options);

        // Migrate rather than EnsureCreated: this is also what proves the
        // generated migration applies against real Postgres at all.
        await context.Database.MigrateAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task Migrate_ShouldLeaveNoPendingMigrations()
    {
        await using var context = new CatalogDbContext(_options);

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
    }

    /// <summary>
    /// A price with four decimal places must survive the round trip exactly.
    /// If the column were a floating-point type this is where it would show up,
    /// and it would show up as an order total nobody agreed to.
    /// </summary>
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
        var stored = await read.Events.SingleAsync(e => e.Id == eventId);

        Assert.Equal(123.4567m, stored.Price);
        Assert.Equal("GBP", stored.Currency);
    }

    /// <summary>
    /// Timestamps are <c>timestamptz</c>, which stores UTC and discards offsets.
    /// What comes back must be the same instant and must still say it is UTC —
    /// a <see cref="DateTimeKind.Unspecified"/> here would silently poison every
    /// comparison downstream, including the on-sale gate in Orders.
    /// </summary>
    [Fact]
    public async Task Timestamps_ShouldRoundTripAsUtc()
    {
        var eventId = Guid.NewGuid();
        var startsAt = new DateTime(2026, 7, 1, 19, 30, 0, DateTimeKind.Utc);
        var onSaleAt = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);

        await using (var write = new CatalogDbContext(_options))
        {
            var @event = NewEvent(eventId, price: 50m);
            @event.StartsAt = startsAt;
            @event.OnSaleAt = onSaleAt;

            write.Events.Add(@event);
            await write.SaveChangesAsync();
        }

        await using var read = new CatalogDbContext(_options);
        var stored = await read.Events.SingleAsync(e => e.Id == eventId);

        Assert.Equal(startsAt, stored.StartsAt);
        Assert.Equal(DateTimeKind.Utc, stored.StartsAt.Kind);
        Assert.Equal(onSaleAt, stored.OnSaleAt);
        Assert.Equal(DateTimeKind.Utc, stored.OnSaleAt!.Value.Kind);
    }

    /// <summary>
    /// Null means "on sale now", not "on sale in the year 1". The column has to
    /// be able to hold that distinction or the gate in Orders cannot read it.
    /// </summary>
    [Fact]
    public async Task OnSaleAt_ShouldBeNullable()
    {
        var eventId = Guid.NewGuid();

        await using (var write = new CatalogDbContext(_options))
        {
            var @event = NewEvent(eventId, price: 50m);
            @event.OnSaleAt = null;

            write.Events.Add(@event);
            await write.SaveChangesAsync();
        }

        await using var read = new CatalogDbContext(_options);

        Assert.Null((await read.Events.SingleAsync(e => e.Id == eventId)).OnSaleAt);
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
        var stored = await read.Venues.SingleAsync(v => v.Id == venueId);

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
