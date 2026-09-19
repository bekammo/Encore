using Encore.Modules.Orders.Data;
using Encore.Modules.Orders.Models;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Orders.IntegrationTests;

/// <summary>
/// Proves Orders' schema enforces what the module is relying on it to enforce.
/// </summary>
/// <remarks>
/// The partial unique index is the one that matters. It is the real guard
/// against a retried <c>POST /orders</c> becoming two orders, and it is
/// expressed as a raw SQL filter string that no compiler checks against
/// <see cref="OrderStatus"/> — so a wrong literal produces a silently different
/// index rather than a build error. These tests are what would catch that.
/// </remarks>
public sealed class OrdersSchemaTests : IAsyncLifetime
{
    private static readonly DateTime PlacedAt = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("encore")
        .WithUsername("encore")
        .WithPassword("encore")
        .Build();

    private DbContextOptions<OrdersDbContext> _options = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        _options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseOrdersNpgsql(_postgres.GetConnectionString())
            .Options;

        await using var context = new OrdersDbContext(_options);

        // Migrate rather than EnsureCreated: this also proves the generated
        // migration applies against real Postgres, including that it does not
        // try to create the xmin system column.
        await context.Database.MigrateAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task Migrate_ShouldLeaveNoPendingMigrations()
    {
        await using var context = new OrdersDbContext(_options);

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
    }

    /// <summary>
    /// The guard that makes a retried checkout safe. Two pending orders for one
    /// client at one event must be impossible at the database, not merely
    /// checked for in code — two requests can both pass a check.
    /// </summary>
    [Fact]
    public async Task SecondPendingOrder_ForSameClientAndEvent_ShouldBeRefused()
    {
        var clientId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        await using (var first = new OrdersDbContext(_options))
        {
            first.Orders.Add(NewOrder(clientId, eventId, OrderStatus.Pending));
            await first.SaveChangesAsync();
        }

        await using var second = new OrdersDbContext(_options);
        second.Orders.Add(NewOrder(clientId, eventId, OrderStatus.Pending));

        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }

    /// <summary>
    /// The index is partial, and the partiality is the point: once an order has
    /// ended, the customer may start another for the same event.
    /// </summary>
    [Theory]
    [InlineData(OrderStatus.Confirmed)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Expired)]
    [InlineData(OrderStatus.Failed)]
    public async Task NewPendingOrder_AfterAnEndedOne_ShouldBeAllowed(OrderStatus ended)
    {
        var clientId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        await using (var first = new OrdersDbContext(_options))
        {
            first.Orders.Add(NewOrder(clientId, eventId, ended));
            await first.SaveChangesAsync();
        }

        await using var second = new OrdersDbContext(_options);
        second.Orders.Add(NewOrder(clientId, eventId, OrderStatus.Pending));

        await second.SaveChangesAsync();

        Assert.Equal(2, await second.Orders.CountAsync(order => order.ClientId == clientId));
    }

    /// <summary>The cap is per event, so the same client may check out at two shows at once.</summary>
    [Fact]
    public async Task PendingOrders_ForDifferentEvents_ShouldBeAllowed()
    {
        var clientId = Guid.NewGuid();

        await using var context = new OrdersDbContext(_options);

        context.Orders.Add(NewOrder(clientId, Guid.NewGuid(), OrderStatus.Pending));
        context.Orders.Add(NewOrder(clientId, Guid.NewGuid(), OrderStatus.Pending));

        await context.SaveChangesAsync();

        Assert.Equal(2, await context.Orders.CountAsync(order => order.ClientId == clientId));
    }

    /// <summary>
    /// Confirm and cancel arriving together really do race this row, which is
    /// why it carries a concurrency token at all. The second writer must lose
    /// rather than overwrite.
    /// </summary>
    [Fact]
    public async Task ConcurrentWrites_ToOneOrder_ShouldBeRefusedForTheLoser()
    {
        var orderId = Guid.NewGuid();

        await using (var seed = new OrdersDbContext(_options))
        {
            var order = NewOrder(Guid.NewGuid(), Guid.NewGuid(), OrderStatus.Pending);
            order.Id = orderId;

            seed.Orders.Add(order);
            await seed.SaveChangesAsync();
        }

        await using var confirming = new OrdersDbContext(_options);
        await using var cancelling = new OrdersDbContext(_options);

        // Both load the same row version before either writes.
        var toConfirm = await confirming.Orders.SingleAsync(order => order.Id == orderId);
        var toCancel = await cancelling.Orders.SingleAsync(order => order.Id == orderId);

        toConfirm.Status = OrderStatus.Confirmed;
        await confirming.SaveChangesAsync();

        toCancel.Status = OrderStatus.Cancelled;

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => cancelling.SaveChangesAsync());
    }

    [Fact]
    public async Task Order_ShouldRoundTripWithItsLines()
    {
        var orderId = Guid.NewGuid();
        var seatId = Guid.NewGuid();

        await using (var write = new OrdersDbContext(_options))
        {
            var order = NewOrder(Guid.NewGuid(), Guid.NewGuid(), OrderStatus.Pending);
            order.Id = orderId;
            order.Total = 84.5000m;
            order.Lines =
            [
                new OrderLine
                {
                    Id = Guid.NewGuid(),
                    OrderId = orderId,
                    SeatId = seatId,
                    UnitPrice = 42.2500m,
                    Currency = "GBP"
                },
                new OrderLine
                {
                    Id = Guid.NewGuid(),
                    OrderId = orderId,
                    SeatId = Guid.NewGuid(),
                    UnitPrice = 42.2500m,
                    Currency = "GBP"
                }
            ];

            write.Orders.Add(order);
            await write.SaveChangesAsync();
        }

        await using var read = new OrdersDbContext(_options);
        var stored = await read.Orders
            .Include(order => order.Lines)
            .SingleAsync(order => order.Id == orderId);

        Assert.Equal(2, stored.Lines.Count);
        Assert.Equal(84.5000m, stored.Total);
        Assert.Contains(stored.Lines, line => line.SeatId == seatId && line.UnitPrice == 42.2500m);
        Assert.Equal(PlacedAt, stored.PlacedAt);
        Assert.Equal(DateTimeKind.Utc, stored.PlacedAt.Kind);
    }

    private static Order NewOrder(Guid clientId, Guid eventId, OrderStatus status) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        EventId = eventId,
        Status = status,
        PlacedAt = PlacedAt,
        HoldsExpireAt = status is OrderStatus.Pending ? PlacedAt.AddMinutes(5) : null,
        ClosedAt = status is OrderStatus.Pending ? null : PlacedAt.AddMinutes(1),
        Total = 0m,
        Currency = "GBP"
    };
}
