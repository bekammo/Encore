using Encore.Modules.Inventory.Contracts.Events;
using Encore.Modules.Notifications.Data;
using Encore.Modules.Notifications.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace Encore.Modules.Notifications.IntegrationTests;

/// <summary>
/// The first consumer of anything this system publishes: it records a sale, and it
/// survives being told about the same sale twice.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotency is the whole subject here.</b> Delivery is at-least-once by
/// design — a dispatcher that dies between handling a message and marking it will
/// redeliver on the next tick, and that is the outbox working rather than failing.
/// So every one of these tests is really asking the same question: does a
/// redelivery cost anything.
/// </para>
/// <para>
/// Real Postgres, because the answer is a unique index rather than any C# in the
/// handler. The read-then-write version of this guard is the one two concurrent
/// deliveries both pass, which is exactly what the last test here demonstrates.
/// </para>
/// </remarks>
public sealed class SeatSoldNotifierTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("encore")
        .WithUsername("encore")
        .WithPassword("encore")
        .Build();

    private DbContextOptions<NotificationsDbContext> _options = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        _options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNotificationsNpgsql(_postgres.GetConnectionString())
            .Options;

        await using var context = new NotificationsDbContext(_options);

        // Migrate rather than EnsureCreated, so this also proves the generated
        // migration applies against real Postgres — including the unique index that
        // every assertion below depends on.
        await context.Database.MigrateAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task Handle_ShouldRecordWhatTheClientShouldBeTold()
    {
        var messageId = Guid.CreateVersion7();
        var sold = Sold();

        await using (var context = new NotificationsDbContext(_options))
        {
            await NotifierFor(context).HandleAsync(sold, messageId, CancellationToken.None);
        }

        var notification = Assert.Single(await ForMessageAsync(messageId));

        Assert.Equal(sold.ClientId, notification.ClientId);
        Assert.Equal(sold.EventId, notification.EventId);
        Assert.Equal(sold.SeatId, notification.SeatId);
        Assert.Equal(NotificationKind.SeatSold, notification.Kind);

        // Copied from the event rather than read from a clock here, so the row says
        // when the sale happened rather than when the backlog got round to it.
        Assert.Equal(sold.OccurredAt, notification.OccurredAt);
        Assert.True(notification.CreatedAt >= sold.OccurredAt);
    }

    /// <summary>
    /// The redelivery case, which the outbox guarantees will happen eventually.
    /// </summary>
    [Fact]
    public async Task Handle_WhenTheSameMessageArrivesTwice_ShouldRecordOneNotification()
    {
        var messageId = Guid.CreateVersion7();
        var sold = Sold();

        for (var delivery = 0; delivery < 2; delivery++)
        {
            await using var context = new NotificationsDbContext(_options);

            await NotifierFor(context).HandleAsync(sold, messageId, CancellationToken.None);
        }

        Assert.Single(await ForMessageAsync(messageId));
    }

    /// <summary>
    /// Two events about the same seat are two notifications, not one.
    /// </summary>
    /// <remarks>
    /// The deduplication key is the message, never the seat. Getting that wrong
    /// would look correct in every test above and would silently swallow the second
    /// of two genuinely different things that happened to one seat — which, once
    /// releases and holds have consumers too, is most of them.
    /// </remarks>
    [Fact]
    public async Task Handle_WhenTwoMessagesDescribeOneSeat_ShouldRecordBoth()
    {
        var sold = Sold();
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        await using (var context = new NotificationsDbContext(_options))
        {
            await NotifierFor(context).HandleAsync(sold, first, CancellationToken.None);
        }

        await using (var context = new NotificationsDbContext(_options))
        {
            await NotifierFor(context).HandleAsync(sold, second, CancellationToken.None);
        }

        Assert.Single(await ForMessageAsync(first));
        Assert.Single(await ForMessageAsync(second));
    }

    /// <summary>
    /// Two deliveries of one message at the same instant still produce one row.
    /// </summary>
    /// <remarks>
    /// This is the test that says the unique index is the guard and the handler's
    /// catch is a courtesy. A read-then-write check would pass every other test in
    /// this file and fail this one, because two deliveries can both read "nothing
    /// there" before either writes. Two dispatchers on two hosts is exactly the
    /// arrangement <c>SKIP LOCKED</c> exists to make possible, so this is not a
    /// hypothetical.
    /// </remarks>
    [Fact]
    public async Task Handle_WhenTwoDeliveriesRace_ShouldRecordOneNotification()
    {
        var messageId = Guid.CreateVersion7();
        var sold = Sold();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var deliveries = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            await using var context = new NotificationsDbContext(_options);
            var notifier = NotifierFor(context);

            await gate.Task;
            await notifier.HandleAsync(sold, messageId, CancellationToken.None);
        }));

        var running = deliveries.ToArray();
        gate.SetResult();

        // Neither delivery may surface an error: under at-least-once semantics a
        // redelivery is the mechanism working, so losing the race is a success.
        await Task.WhenAll(running);

        Assert.Single(await ForMessageAsync(messageId));
    }

    private static SeatSoldV1 Sold() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Truncated(DateTime.UtcNow));

    private static SeatSoldNotifier NotifierFor(NotificationsDbContext context) =>
        new(context, TimeProvider.System, NullLogger<SeatSoldNotifier>.Instance);

    private async Task<List<Notification>> ForMessageAsync(Guid messageId)
    {
        await using var context = new NotificationsDbContext(_options);

        return await context.Notifications.AsNoTracking()
            .Where(notification => notification.MessageId == messageId)
            .ToListAsync();
    }

    /// <summary>
    /// Truncated to whole microseconds: Postgres <c>timestamptz</c> resolves to a
    /// microsecond while <see cref="DateTime"/> ticks are 100ns, so an untruncated
    /// instant comes back slightly different from what went in.
    /// </summary>
    private static DateTime Truncated(DateTime value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMicrosecond), DateTimeKind.Utc);
}
