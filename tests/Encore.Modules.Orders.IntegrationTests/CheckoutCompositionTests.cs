using Encore.Modules.Catalog.Contracts;
using Encore.Modules.Inventory;
using Encore.Modules.Inventory.Adapters.Persistence;
using Encore.Modules.Inventory.Application;
using Encore.Modules.Inventory.Contracts;
using Encore.Modules.Orders.Data;
using Encore.Modules.Payments;
using Encore.Modules.Payments.Contracts;
using Encore.Modules.Payments.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Orders.IntegrationTests;

/// <summary>
/// Checkout, confirm and cancel through the real Inventory and Payments modules, composed
/// through their <c>Add*</c> methods as the host composes them. Every other Orders test fakes
/// the far side of its seams, so this is the one place 011 and 012 are checked across them:
/// 018's lesson that a seam is not proven by testing each side of it.
/// </summary>
/// <remarks>
/// The invariants are chaos.sh's order evidence, read from the same tables, so the claim in
/// WRITEUP.md that no order ends partly sold, sold without money or paid without its seats
/// is a check that fails here rather than only a number in a report.
/// </remarks>
public sealed class CheckoutCompositionTests : IAsyncLifetime
{
    private const int OrderCount = 20;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("encore")
        .WithUsername("encore")
        .WithPassword("encore")
        .Build();

    private ServiceProvider _provider = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var connectionString = _postgres.GetConnectionString();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Inventory"] = connectionString,
                ["ConnectionStrings:Payments"] = connectionString,
                ["ConnectionStrings:Orders"] = connectionString,

                // Never dialled: the hold cap's lock is Postgres here, so no Redis is needed.
                ["ConnectionStrings:Redis"] = "localhost:1",
                ["Inventory:HoldCapLock"] = "Postgres",

                // A gateway that answers, promptly and yes, so every ending is decided by the race.
                ["Payments:Simulation:TimeoutRate"] = "0",
                ["Payments:Simulation:DeclineRate"] = "0",
                ["Payments:Simulation:MinLatency"] = "00:00:00",
                ["Payments:Simulation:MaxLatency"] = "00:00:00.020"
            })
            .Build();

        var services = new ServiceCollection();

        services.AddLogging();
        services.AddInventoryModule(configuration);
        services.AddPaymentsModule(configuration);
        services.AddOrdersModule(configuration);

        // Catalog is not the seam under test.
        services.AddSingleton<IEventPricing, OnSale>();

        _provider = services.BuildServiceProvider();

        await using var scope = _provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<InventoryDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<OrdersDbContext>().Database.MigrateAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    /// <summary>
    /// Every order's confirm races its cancel. Whichever wins, the seats and the money end
    /// together: no order partly sold, none sold without money, none paid without its seats.
    /// </summary>
    [Fact]
    public async Task ConfirmRacingCancel_ShouldEndEveryOrderWithItsSeatsAndMoneyTogether()
    {
        var eventId = Guid.NewGuid();
        var seatIds = await SeatMapAsync(eventId, OrderCount * 2);

        var orders = new List<(Guid ClientId, Guid OrderId)>();

        for (var i = 0; i < OrderCount; i++)
        {
            var clientId = Guid.NewGuid();

            var placed = await WithCheckoutAsync(checkout =>
                checkout.CheckoutAsync(clientId, eventId, [seatIds[2 * i], seatIds[(2 * i) + 1]]));

            Assert.Equal(CheckoutOutcome.Created, placed.Outcome);
            orders.Add((clientId, placed.Order!.Id));
        }

        await Task.WhenAll(orders.SelectMany(order => new[]
        {
            Task.Run(() => WithCheckoutAsync(checkout => checkout.ConfirmAsync(order.ClientId, order.OrderId))),
            Task.Run(() => WithCheckoutAsync(checkout => checkout.CancelAsync(order.ClientId, order.OrderId)))
        }));

        var evidence = await OrderEvidenceAsync();

        Assert.Equal(OrderCount, evidence["orders"]);
        Assert.All(
            new[] { "partly_sold", "sold_not_confirmed", "sold_no_money", "paid_not_sold", "confirmed_not_captured" },
            invariant => Assert.True(evidence[invariant] == 0, $"{invariant}: {evidence[invariant]}"));
    }

    /// <summary>
    /// A confirm has sold the seats but not yet recorded it when the cancel arrives. The real
    /// adapter must answer sold-to-you, so the cancel backs off and leaves the money (012).
    /// Answering "already sold" instead would void an authorisation with a sale behind it.
    /// </summary>
    [Fact]
    public async Task Cancel_AfterTheSaleButBeforeItIsRecorded_ShouldLeaveTheMoneyForTheConfirm()
    {
        var eventId = Guid.NewGuid();
        var seatIds = await SeatMapAsync(eventId, 2);
        var clientId = Guid.NewGuid();

        var placed = await WithCheckoutAsync(checkout => checkout.CheckoutAsync(clientId, eventId, seatIds));
        var order = placed.Order!;

        // A confirm's first two steps, through the real modules, stopped before it records the sale.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var payments = scope.ServiceProvider.GetRequiredService<IOrderPayments>();
            var seats = scope.ServiceProvider.GetRequiredService<ISeatReservations>();

            await payments.AuthorizeAsync(new AuthorizePaymentRequest(order.Id, clientId, order.Total, order.Currency));
            Assert.True((await seats.SellAsync(new SellSeatsRequest(eventId, seatIds, clientId))).AllSold);
        }

        var cancel = await WithCheckoutAsync(checkout => checkout.CancelAsync(clientId, order.Id));
        Assert.Equal(OrderActionOutcome.LostRace, cancel.Outcome);

        // The confirm, retried, finds the money still held and finishes.
        var confirm = await WithCheckoutAsync(checkout => checkout.ConfirmAsync(clientId, order.Id));
        Assert.Equal(OrderActionOutcome.Completed, confirm.Outcome);

        var evidence = await OrderEvidenceAsync();
        Assert.Equal(0, evidence["paid_not_sold"]);
        Assert.Equal(0, evidence["sold_no_money"]);
        Assert.Equal(0, evidence["confirmed_not_captured"]);
    }

    // -- Helpers ----------------------------------------------------------

    private async Task<T> WithCheckoutAsync<T>(Func<CheckoutService, Task<T>> action)
    {
        await using var scope = _provider.CreateAsyncScope();

        return await action(scope.ServiceProvider.GetRequiredService<CheckoutService>());
    }

    private async Task<IReadOnlyList<Guid>> SeatMapAsync(Guid eventId, int count)
    {
        await using var scope = _provider.CreateAsyncScope();

        var created = await scope.ServiceProvider
            .GetRequiredService<CreateSeatMapCommandHandler>()
            .HandleAsync(new CreateSeatMapCommand(eventId, count));

        return created.SeatIds;
    }

    /// <summary>
    /// chaos.sh's order invariants, over the same tables. A seat is this order's sale when it
    /// is sold to the order's client: every order here has its own client.
    /// </summary>
    private async Task<Dictionary<string, long>> OrderEvidenceAsync()
    {
        const string Sql = """
            WITH per_order AS (
              SELECT o."Id" AS order_id,
                     o."Status" AS status,
                     count(*) AS seats,
                     count(*) FILTER (WHERE s."Status" = 2 AND s."HeldByClientId" = o."ClientId") AS sold
              FROM orders.orders o
              JOIN orders.order_lines l ON l."OrderId" = o."Id"
              JOIN inventory.seats s ON s."Id" = l."SeatId"
              GROUP BY o."Id", o."Status"
            ),
            money AS (
              SELECT "OrderId",
                     bool_or("Status" = 2) AS captured,
                     bool_or("Status" = 1) AS authorized
              FROM payments.payments
              GROUP BY "OrderId"
            ),
            checked AS (
              SELECT per_order.*,
                     coalesce(money.captured, false) AS captured,
                     coalesce(money.authorized, false) AS authorized
              FROM per_order
              LEFT JOIN money ON money."OrderId" = per_order.order_id
            )
            SELECT
              count(*) AS orders,
              count(*) FILTER (WHERE sold > 0 AND sold < seats) AS partly_sold,
              count(*) FILTER (WHERE sold > 0 AND status NOT IN (1, 5)) AS sold_not_confirmed,
              count(*) FILTER (WHERE sold > 0 AND NOT captured AND NOT authorized) AS sold_no_money,
              count(*) FILTER (WHERE captured AND sold < seats) AS paid_not_sold,
              count(*) FILTER (WHERE status = 1 AND NOT captured) AS confirmed_not_captured
            FROM checked;
            """;

        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(Sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        return Enumerable.Range(0, reader.FieldCount)
            .ToDictionary(reader.GetName, ordinal => reader.GetInt64(ordinal));
    }

    /// <summary>Catalog at its contract: priced, and on sale since long ago.</summary>
    private sealed class OnSale : IEventPricing
    {
        public Task<EventPricingResponse> GetAsync(
            EventPricingRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(EventPricingResponse.Priced(
                25m,
                "GBP",
                DateTime.UnixEpoch,
                DateTime.UtcNow.AddMonths(1)));
    }
}
