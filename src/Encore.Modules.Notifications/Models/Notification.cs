namespace Encore.Modules.Notifications.Models;

/// <summary>
/// A record that this client should be told something, and what about.
/// </summary>
/// <remarks>
/// A plain POCO: it is written once and never transitions. No message body; no channel
/// exists yet to decide the wording.
/// </remarks>
public sealed class Notification
{
    public Guid Id { get; set; }

    /// <summary>
    /// The publisher's message id: the deduplication key, the same on every redelivery.
    /// </summary>
    public Guid MessageId { get; set; }

    /// <summary>Who is being told. The same claimed identity the rest of the system uses.</summary>
    public Guid ClientId { get; set; }

    /// <summary>The concert the seat belongs to.</summary>
    public Guid EventId { get; set; }

    /// <summary>The seat in question.</summary>
    public Guid SeatId { get; set; }

    /// <summary>What happened.</summary>
    public NotificationKind Kind { get; set; }

    /// <summary>
    /// When the sale happened, copied from the event rather than read from a local clock.
    /// </summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>When this row was written. The gap to <see cref="OccurredAt"/> is delivery latency.</summary>
    public DateTime CreatedAt { get; set; }
}
