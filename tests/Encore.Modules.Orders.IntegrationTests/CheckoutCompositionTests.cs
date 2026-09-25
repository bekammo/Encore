using Encore.Modules.Catalog.Contracts;
using Encore.Modules.Inventory;
using Encore.Modules.Inventory.Application;
using Encore.Modules.Inventory.Contracts;
using Encore.Modules.Payments;
using Encore.Modules.Payments.Contracts;
using Encore.Modules.Orders.Data;
using Encore.Modules.Orders.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Encore.Modules.Orders.IntegrationTests;

/// <summary>
/// The one place 011 and 012 are checked across Orders' seams, through the real Inventory and
/// Payments modules composed as the host composes them (018). The invariants count every order,
/// so the shared database is emptied before each test.
/// </summary>
public sealed class CheckoutCompositionTests(OrdersDatabase database)
    : IClassFixture<OrdersDatabase>, IAsyncLifetime
{
    private const int OrderCount = 20;

    private readonly OrdersDatabase _database = database;

    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        await _database.ResetAsync();

        _provider = Compose(clock: null);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    private ServiceProvider Compose(TimeProvider? clock)
    {
        var connectionString = _database.ConnectionString;

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

                // The gateway waits on the injected clock, which a fake clock never advances.
                ["Payments:Simulation:MaxLatency"] = clock is null ? "00:00:00.020" : "00:00:00"
            })
            .Build();

        var services = new ServiceCollection();

        // Before the modules, whose TryAdd leaves it in place, so one clock lapses every hold.
        if (clock is not null)
        {
            services.AddSingleton(clock);
        }

        services.AddLogging();
        services.AddInventoryModule(configuration);
        services.AddPaymentsModule(configuration);
        services.AddOrdersModule(configuration);

        services.AddSingleton<IEventPricing, OnSale>();

        return services.BuildServiceProvider();
    }

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
            new[] { "partly_sold", "sold_not_confirmed", "sold_no_money", "paid_not_sold", "confirmed_not_captured", "ended_still_authorized" },
            invariant => Assert.True(evidence[invariant] == 0, $"{invariant}: {evidence[invariant]}"));
    }

    /// <summary>
    /// The real adapter must answer SoldToYou, so the cancel backs off (012); AlreadySold would
    /// void an authorisation with a sale behind it.
    /// </summary>
    [Fact]
    public async Task Cancel_AfterTheSaleButBeforeItIsRecorded_ShouldLeaveTheMoneyForTheConfirm()
    {
        var eventId = Guid.NewGuid();
        var seatIds = await SeatMapAsync(eventId, 2);
        var clientId = Guid.NewGuid();

        var placed = await WithCheckoutAsync(checkout => checkout.CheckoutAsync(clientId, eventId, seatIds));
        var order = placed.Order!;

        // A confirm's authorise and sell, stopped before it records the sale.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var payments = scope.ServiceProvider.GetRequiredService<IOrderPayments>();
            var seats = scope.ServiceProvider.GetRequiredService<ISeatReservations>();

            await payments.AuthorizeAsync(new AuthorizePaymentRequest(order.Id, clientId, order.Total, order.Currency));
            Assert.True((await seats.SellAsync(new SellSeatsRequest(eventId, seatIds, clientId))).AllSold);
        }

        var cancel = await WithCheckoutAsync(checkout => checkout.CancelAsync(clientId, order.Id));
        Assert.Equal(OrderActionOutcome.LostRace, cancel.Outcome);

        var confirm = await WithCheckoutAsync(checkout => checkout.ConfirmAsync(clientId, order.Id));
        Assert.Equal(OrderActionOutcome.Completed, confirm.Outcome);

        var evidence = await OrderEvidenceAsync();
        Assert.Equal(0, evidence["paid_not_sold"]);
        Assert.Equal(0, evidence["sold_no_money"]);
        Assert.Equal(0, evidence["confirmed_not_captured"]);
    }

    /// <summary>
    /// An order authorised, then abandoned: once its holds lapse the sweep hands the seats back
    /// and releases the money through the real modules (031).
    /// </summary>
    [Fact]
    public async Task ExpirySweep_WhenAnAuthorisedOrderIsAbandoned_ShouldFreeItsSeatsAndItsMoney()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await _provider.DisposeAsync();
        _provider = Compose(clock);

        var eventId = Guid.NewGuid();
        var seatIds = await SeatMapAsync(eventId, 2);
        var clientId = Guid.NewGuid();

        var placed = await WithCheckoutAsync(checkout => checkout.CheckoutAsync(clientId, eventId, seatIds));
        var order = placed.Order!;

        // A confirm that authorised and then died before it asked for the seats.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var authorized = await scope.ServiceProvider
                .GetRequiredService<IOrderPayments>()
                .AuthorizeAsync(new AuthorizePaymentRequest(order.Id, clientId, order.Total, order.Currency));

            Assert.Equal(AuthorizePaymentStatus.Authorized, authorized.Status);
        }

        // Past the five-minute holds and the sweep's grace.
        clock.Advance(TimeSpan.FromMinutes(7));

        var sweeper = new OrderExpirySweeper(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new OrderExpirySweepOptions()),
            clock,
            NullLogger<OrderExpirySweeper>.Instance);

        Assert.Equal(1, await sweeper.SweepBatchAsync(CancellationToken.None));

        var evidence = await OrderEvidenceAsync();
        Assert.Equal(0, evidence["ended_still_authorized"]);
        Assert.Equal(0, evidence["sold_not_confirmed"]);

        var expired = await WithCheckoutAsync(checkout => checkout.ConfirmAsync(clientId, order.Id));
        Assert.Equal(OrderActionOutcome.Completed, expired.Outcome);
        Assert.Equal(OrderStatus.Expired, expired.Order!.Status);

        // Another customer can have the seats.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var held = await scope.ServiceProvider
                .GetRequiredService<ISeatReservations>()
                .HoldAsync(new HoldSeatsRequest(eventId, seatIds, Guid.NewGuid()));

            Assert.All(held.Seats, seat => Assert.Equal(HoldSeatStatus.Held, seat.Status));
        }
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

        return await scope.ServiceProvider
            .GetRequiredService<CreateSeatMapCommandHandler>()
            .HandleAsync(new CreateSeatMapCommand(eventId, count));
    }

    // chaos.sh's order invariants over the same tables. A sold seat counts as an order's sale when
    // it is sold to the order's client, so every order here needs a client of its own.
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
              count(*) FILTER (WHERE status = 1 AND NOT captured) AS confirmed_not_captured,
              count(*) FILTER (WHERE status IN (2, 3, 4) AND authorized) AS ended_still_authorized
            FROM checked;
            """;

        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(Sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        return Enumerable.Range(0, reader.FieldCount)
            .ToDictionary(reader.GetName, ordinal => reader.GetInt64(ordinal));
    }

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
