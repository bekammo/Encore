namespace Encore.Modules.Notifications.Models;

/// <summary>No message body: no channel exists yet to decide the wording.</summary>
public sealed class Notification
{
    public Guid Id { get; set; }

    public Guid MessageId { get; set; }

    public Guid ClientId { get; set; }

    public Guid EventId { get; set; }

    public Guid SeatId { get; set; }

    public NotificationKind Kind { get; set; }

    public DateTime OccurredAt { get; set; }

    /// <summary>The gap from <see cref="OccurredAt"/> is delivery latency (016).</summary>
    public DateTime CreatedAt { get; set; }
}
