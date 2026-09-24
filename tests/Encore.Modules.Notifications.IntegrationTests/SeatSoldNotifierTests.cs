using Encore.Modules.Inventory.Contracts.Events;
using Encore.Modules.Notifications.Data;
using Encore.Modules.Notifications.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Encore.Modules.Notifications.IntegrationTests;

/// <summary>Real Postgres: the redelivery guard is a unique index, not handler code (016).</summary>
public sealed class SeatSoldNotifierTests(NotificationsDatabase database) : IClassFixture<NotificationsDatabase>
{
    private readonly DbContextOptions<NotificationsDbContext> _options = database.Options;

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

        Assert.Equal(sold.OccurredAt, notification.OccurredAt);
        Assert.True(notification.CreatedAt >= sold.OccurredAt);
    }

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

    /// <summary>A read-then-write check would fail this.</summary>
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

        // Neither delivery surfaces an error: losing the race is a success.
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

    // Postgres keeps microseconds, so OccurredAt would not round-trip equal without this.
    private static DateTime Truncated(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerMicrosecond, DateTimeKind.Utc);
}
