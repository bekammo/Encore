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

/// <summary>
/// The capture sweep against real Postgres, with Payments faked at its contract: which orders
/// it finishes, which it leaves alone, and that it only ever does what a confirm would. A sweep
/// visits every owed order, so the shared database is emptied before each test.
/// </summary>
public sealed class CaptureSweeperTests(OrdersDatabase database)
    : IClassFixture<OrdersDatabase>, IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Comfortably past <see cref="CaptureSweepOptions.MinimumAge"/>.</summary>
    private static readonly DateTime LongAgo = Now.AddMinutes(-10);

    private readonly OrdersDatabase _database = database;

    private readonly CountingPayments _payments = new();

    private readonly DbContextOptions<OrdersDbContext> _options = database.Options;

    /// <summary>Empties the orders the previous test left.</summary>
    public Task InitializeAsync() => _database.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>An order owed its capture for long enough is captured and confirmed.</summary>
    [Fact]
    public async Task Sweep_WhenAnOrderHasAwaitedItsCapture_ShouldCaptureAndConfirmIt()
    {
        var orderId = await SeedAsync(OrderStatus.AwaitingCapture, soldAt: LongAgo);

        await using var host = Host();

        Assert.Equal(1, await host.Sweeper.SweepBatchAsync(CancellationToken.None));

        var stored = await ReadAsync(orderId);
        Assert.Equal(OrderStatus.Confirmed, stored.Status);
        Assert.Equal(Now, stored.ClosedAt);
        Assert.Single(_payments.Captures);
    }

    /// <summary>A sale recorded moments ago belongs to the confirm still asking for its capture.</summary>
    [Fact]
    public async Task Sweep_WhenTheSaleIsRecent_ShouldLeaveItToItsConfirm()
    {
        var orderId = await SeedAsync(OrderStatus.AwaitingCapture, soldAt: Now.AddSeconds(-10));

        await using var host = Host();

        Assert.Equal(0, await host.Sweeper.SweepBatchAsync(CancellationToken.None));
        Assert.Equal(OrderStatus.AwaitingCapture, (await ReadAsync(orderId)).Status);
        Assert.Empty(_payments.Captures);
    }

    /// <summary>A capture unanswered again leaves the order owed, to be asked about next sweep.</summary>
    [Fact]
    public async Task Sweep_WhenTheCaptureGoesUnansweredAgain_ShouldLeaveTheOrderOwed()
    {
        var orderId = await SeedAsync(OrderStatus.AwaitingCapture, soldAt: LongAgo);
        _payments.CaptureWith = CapturePaymentStatus.TimedOut;

        await using var host = Host();

        Assert.Equal(0, await host.Sweeper.SweepBatchAsync(CancellationToken.None));

        var stored = await ReadAsync(orderId);
        Assert.Equal(OrderStatus.AwaitingCapture, stored.Status);
        Assert.Equal(LongAgo, stored.SoldAt);
        Assert.Null(stored.ClosedAt);
    }

    /// <summary>Only orders awaiting capture are its business.</summary>
    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.Confirmed)]
    [InlineData(OrderStatus.Failed)]
    public async Task Sweep_ShouldNotTouchAnOrderThatIsNotOwed(OrderStatus status)
    {
        var orderId = await SeedAsync(status, soldAt: LongAgo);

        await using var host = Host();

        Assert.Equal(0, await host.Sweeper.SweepBatchAsync(CancellationToken.None));
        Assert.Equal(status, (await ReadAsync(orderId)).Status);
        Assert.Empty(_payments.Captures);
    }

    /// <summary>While another connection holds the lease, a sweep does nothing.</summary>
    [Fact]
    public async Task Sweep_WhenAnotherInstanceHoldsTheLease_ShouldDoNothing()
    {
        await SeedAsync(OrderStatus.AwaitingCapture, soldAt: LongAgo);

        await using var host = Host();

        await using (var holder = new OrdersDbContext(_options))
        await using (await holder.Database.BeginTransactionAsync())
        {
            await holder.Database
                .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({CaptureSweeper.LeaseKey}) AS \"Value\"")
                .SingleAsync();

            Assert.Equal(0, await host.Sweeper.SweepBatchAsync(CancellationToken.None));
            Assert.Empty(_payments.Captures);
        }

        Assert.Equal(1, await host.Sweeper.SweepBatchAsync(CancellationToken.None));
    }

    // -- Helpers ----------------------------------------------------------

    /// <summary>A committed order in the given status, with one line.</summary>
    private async Task<Guid> SeedAsync(OrderStatus status, DateTime? soldAt)
    {
        var orderId = Guid.NewGuid();

        await using var context = new OrdersDbContext(_options);

        context.Orders.Add(new Order
        {
            Id = orderId,
            ClientId = Guid.NewGuid(),
            EventId = Guid.NewGuid(),
            Status = status,
            PlacedAt = LongAgo.AddMinutes(-1),
            SoldAt = soldAt,
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

    /// <summary>The sweep and a checkout service per scope, as the module composes them.</summary>
    private SweeperHost Host()
    {
        var services = new ServiceCollection();
        var clock = new FakeTimeProvider(Now);

        services.AddDbContext<OrdersDbContext>(builder => builder.UseOrdersNpgsql(_database.ConnectionString));
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IOrderPayments>(_payments);
        services.AddSingleton<IEventPricing, Unused>();
        services.AddSingleton<ISeatReservations, Unused>();
        services.AddScoped<CheckoutService>();

        var provider = services.BuildServiceProvider();

        var sweeper = new CaptureSweeper(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new CaptureSweepOptions()),
            clock,
            NullLogger<CaptureSweeper>.Instance);

        return new SweeperHost(provider, sweeper);
    }

    private sealed class SweeperHost(ServiceProvider provider, CaptureSweeper sweeper) : IAsyncDisposable
    {
        internal CaptureSweeper Sweeper { get; } = sweeper;

        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }

    /// <summary>Payments at its contract, counting captures.</summary>
    private sealed class CountingPayments : IOrderPayments
    {
        public CapturePaymentStatus CaptureWith { get; set; } = CapturePaymentStatus.Captured;

        public List<CapturePaymentRequest> Captures { get; } = [];

        public Task<AuthorizePaymentResponse> AuthorizeAsync(
            AuthorizePaymentRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("An order awaiting capture is already authorised.");

        public Task<CapturePaymentResponse> CaptureAsync(
            CapturePaymentRequest request,
            CancellationToken cancellationToken = default)
        {
            Captures.Add(request);
            return Task.FromResult(new CapturePaymentResponse(CaptureWith, Guid.NewGuid()));
        }

        public Task<VoidPaymentResponse> VoidAsync(
            VoidPaymentRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The sweep never voids.");
    }

    /// <summary>Catalog and Inventory, which finishing a capture never asks.</summary>
    private sealed class Unused : IEventPricing, ISeatReservations
    {
        public Task<EventPricingResponse> GetAsync(EventPricingRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<HoldSeatsResponse> HoldAsync(HoldSeatsRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SellSeatsResponse> SellAsync(SellSeatsRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ReleaseSeatsResponse> ReleaseAsync(ReleaseSeatsRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
