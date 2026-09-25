using Encore.Modules.Catalog.Contracts;
using Encore.Modules.Inventory.Contracts;
using Encore.Modules.Orders.Data;
using Encore.Modules.Orders.Models;
using Encore.Modules.Payments.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Encore.Modules.Orders.IntegrationTests;

/// <summary>The sweep visits every lapsed order, so the shared database is emptied before each test (031).</summary>
public sealed class OrderExpirySweeperTests : IClassFixture<OrdersDatabase>, IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // Comfortably past OrderExpirySweepOptions.Grace.
    private static readonly DateTime LongAgo = Now.AddMinutes(-10);

    private readonly OrdersDatabase _database;

    private readonly DbContextOptions<OrdersDbContext> _options;

    // Both fakes write here, so a test can read the order of seats and money.
    private readonly List<string> _calls = [];

    private readonly ScriptedSeats _seats;

    private readonly ScriptedPayments _payments;

    public OrderExpirySweeperTests(OrdersDatabase database)
    {
        _database = database;
        _options = database.Options;
        _seats = new ScriptedSeats(_calls);
        _payments = new ScriptedPayments(_calls);
    }

    public Task InitializeAsync() => _database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Sweep_WhenAPendingOrderHoldsHaveLapsed_ShouldReleaseThenVoidThenExpireIt()
    {
        var orderId = await SeedAsync(OrderStatus.Pending, holdsExpireAt: LongAgo);

        await using var host = Host();

        Assert.Equal(1, await host.Sweeper.SweepBatchAsync(CancellationToken.None));

        var stored = await ReadAsync(orderId);
        Assert.Equal(OrderStatus.Expired, stored.Status);
        Assert.Equal(Now, stored.ClosedAt);
        Assert.Null(stored.HoldsExpireAt);

        // Seats before money, so no sale can follow the void (012).
        Assert.Equal(new[] { "release", "void" }, _calls);
    }

    [Fact]
    public async Task Sweep_WhenTheHoldsLapsedWithinTheGrace_ShouldLeaveTheOrder()
    {
        var orderId = await SeedAsync(OrderStatus.Pending, holdsExpireAt: Now.AddSeconds(-10));

        await using var host = Host();

        Assert.Equal(0, await host.Sweeper.SweepBatchAsync(CancellationToken.None));
        Assert.Equal(OrderStatus.Pending, (await ReadAsync(orderId)).Status);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Sweep_WhenTheReleaseLosesItsRace_ShouldLeaveTheOrderForTheNextSweep()
    {
        var orderId = await SeedAsync(OrderStatus.Pending, holdsExpireAt: LongAgo);
        _seats.ReleaseWith = ReleaseSeatStatus.LostRace;

        await using var host = Host();

        Assert.Equal(0, await host.Sweeper.SweepBatchAsync(CancellationToken.None));
        Assert.Equal(OrderStatus.Pending, (await ReadAsync(orderId)).Status);
        Assert.Equal(new[] { "release" }, _calls);
    }

    /// <summary>A confirm sold the seats and died before recording it: the sale stands (025).</summary>
    [Fact]
    public async Task Sweep_WhenTheSeatsWereSoldButNotRecorded_ShouldFinishTheConfirmRatherThanVoid()
    {
        var orderId = await SeedAsync(OrderStatus.Pending, holdsExpireAt: LongAgo);
        _seats.ReleaseWith = ReleaseSeatStatus.SoldToYou;

        await using var host = Host();

        Assert.Equal(0, await host.Sweeper.SweepBatchAsync(CancellationToken.None));
        Assert.Equal(OrderStatus.Confirmed, (await ReadAsync(orderId)).Status);
        Assert.Equal(new[] { "release", "authorize", "sell", "capture" }, _calls);
    }

    [Theory]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Confirmed)]
    [InlineData(OrderStatus.AwaitingCapture)]
    [InlineData(OrderStatus.Expired)]
    public async Task Sweep_ShouldNotTouchAnOrderThatIsNotPending(OrderStatus status)
    {
        var orderId = await SeedAsync(status, holdsExpireAt: LongAgo);

        await using var host = Host();

        Assert.Equal(0, await host.Sweeper.SweepBatchAsync(CancellationToken.None));
        Assert.Equal(status, (await ReadAsync(orderId)).Status);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Confirm_AfterTheSweepExpiredTheOrder_ShouldAnswerAsIfItFoundTheHoldsLapsed()
    {
        var orderId = await SeedAsync(OrderStatus.Pending, holdsExpireAt: LongAgo);

        await using var host = Host();
        await host.Sweeper.SweepBatchAsync(CancellationToken.None);

        using var scope = host.Provider.CreateScope();
        var clientId = (await ReadAsync(orderId)).ClientId;

        var result = await scope.ServiceProvider
            .GetRequiredService<CheckoutService>()
            .ConfirmAsync(clientId, orderId);

        Assert.Equal(OrderActionOutcome.Completed, result.Outcome);
        Assert.Equal(OrderStatus.Expired, result.Order!.Status);
    }

    [Fact]
    public async Task Sweep_WhenAnotherInstanceHoldsTheLease_ShouldDoNothing()
    {
        await SeedAsync(OrderStatus.Pending, holdsExpireAt: LongAgo);

        await using var host = Host();

        await using (var holder = new OrdersDbContext(_options))
        await using (await holder.Database.BeginTransactionAsync())
        {
            await holder.Database
                .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({OrderExpirySweeper.LeaseKey}) AS \"Value\"")
                .SingleAsync();

            Assert.Equal(0, await host.Sweeper.SweepBatchAsync(CancellationToken.None));
            Assert.Empty(_calls);
        }

        Assert.Equal(1, await host.Sweeper.SweepBatchAsync(CancellationToken.None));
    }

    // -- Helpers ----------------------------------------------------------

    private async Task<Guid> SeedAsync(OrderStatus status, DateTime? holdsExpireAt)
    {
        var orderId = Guid.NewGuid();

        await using var context = new OrdersDbContext(_options);

        context.Orders.Add(new Order
        {
            Id = orderId,
            ClientId = Guid.NewGuid(),
            EventId = Guid.NewGuid(),
            Status = status,
            PlacedAt = LongAgo.AddMinutes(-5),
            HoldsExpireAt = holdsExpireAt,
            Total = 25m,
            Currency = "GBP",
            Lines = [new OrderLine { Id = Guid.NewGuid(), OrderId = orderId, SeatId = Guid.NewGuid(), UnitPrice = 25m, Currency = "GBP" }]
        });

        await context.SaveChangesAsync();

        return orderId;
    }

    private async Task<Order> ReadAsync(Guid orderId)
    {
        await using var context = new OrdersDbContext(_options);

        return await context.Orders.AsNoTracking().SingleAsync(order => order.Id == orderId);
    }

    private SweeperHost Host()
    {
        var services = new ServiceCollection();
        var clock = new FakeTimeProvider(Now);

        services.AddDbContext<OrdersDbContext>(builder => builder.UseOrdersNpgsql(_database.ConnectionString));
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IOrderPayments>(_payments);
        services.AddSingleton<ISeatReservations>(_seats);
        services.AddSingleton<IEventPricing, UnusedPricing>();
        services.AddScoped<CheckoutService>();

        var provider = services.BuildServiceProvider();

        var sweeper = new OrderExpirySweeper(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new OrderExpirySweepOptions()),
            clock,
            NullLogger<OrderExpirySweeper>.Instance);

        return new SweeperHost(provider, sweeper);
    }

    private sealed class SweeperHost(ServiceProvider provider, OrderExpirySweeper sweeper) : IAsyncDisposable
    {
        internal ServiceProvider Provider { get; } = provider;

        internal OrderExpirySweeper Sweeper { get; } = sweeper;

        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }

    private sealed class ScriptedSeats(List<string> calls) : ISeatReservations
    {
        public ReleaseSeatStatus ReleaseWith { get; set; } = ReleaseSeatStatus.Released;

        public Task<ReleaseSeatsResponse> ReleaseAsync(
            ReleaseSeatsRequest request,
            CancellationToken cancellationToken = default)
        {
            calls.Add("release");
            return Task.FromResult(new ReleaseSeatsResponse(
                [.. request.SeatIds.Select(seatId => new ReleaseSeatResponse(seatId, ReleaseWith))]));
        }

        // Only the heal path sells, and a seat already sold to this client sells again (011).
        public Task<SellSeatsResponse> SellAsync(
            SellSeatsRequest request,
            CancellationToken cancellationToken = default)
        {
            calls.Add("sell");
            return Task.FromResult(new SellSeatsResponse([]));
        }

        public Task<HoldSeatsResponse> HoldAsync(
            HoldSeatsRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The sweep never holds.");
    }

    private sealed class ScriptedPayments(List<string> calls) : IOrderPayments
    {
        private readonly Guid _paymentId = Guid.NewGuid();

        public Task<AuthorizePaymentResponse> AuthorizeAsync(
            AuthorizePaymentRequest request,
            CancellationToken cancellationToken = default)
        {
            calls.Add("authorize");
            return Task.FromResult(AuthorizePaymentResponse.Authorized(_paymentId));
        }

        public Task<CapturePaymentResponse> CaptureAsync(
            CapturePaymentRequest request,
            CancellationToken cancellationToken = default)
        {
            calls.Add("capture");
            return Task.FromResult(CapturePaymentResponse.Captured(_paymentId));
        }

        public Task<VoidPaymentResponse> VoidAsync(
            VoidPaymentRequest request,
            CancellationToken cancellationToken = default)
        {
            calls.Add("void");
            return Task.FromResult(VoidPaymentResponse.Voided(_paymentId));
        }
    }

    private sealed class UnusedPricing : IEventPricing
    {
        public Task<EventPricingResponse> GetAsync(
            EventPricingRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
