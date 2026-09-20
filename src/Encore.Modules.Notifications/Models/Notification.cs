namespace Encore.Modules.Notifications.Models;

/// <summary>
/// A record that this client should be told something, and what about.
/// </summary>
/// <remarks>
/// <para>
/// <b>Public setters, like <c>Order</c> and unlike <c>Payment</c>.</b> 029 draws
/// the line by asking whether a type has rules decidable from its own row. A
/// payment has three; this has none — a notification is written once, never
/// transitions, and nothing about it can be wrong in a way a method could refuse.
/// A factory here would be the ceremony 001 argues against.
/// </para>
/// <para>
/// <b>There is no message body, and that is deliberate rather than unfinished.</b>
/// Nothing in this system sends email, so a rendered subject line would be a
/// guess at a format no channel has asked for — and a copy of facts that live in
/// Catalog, which is the drift <c>OrderLine.Description</c> was dropped to avoid
/// (021). What this table is for is the claim that the event arrived: who, which
/// seat, when. A channel arrives with its own opinion about wording.
/// </para>
/// </remarks>
public sealed class Notification
{
    /// <summary>Identity, assigned when the event is handled.</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The outbox message this came from. The deduplication key.
    /// </summary>
    /// <remarks>
    /// Delivery is at-least-once, so this handler can be called twice for one
    /// event — a dispatcher that died between handling and marking will redeliver,
    /// and that is the outbox working rather than failing. The unique index on
    /// this column is what makes the second call a no-op instead of a second
    /// notification. It is the publisher's id rather than one minted here on
    /// purpose: an id this module generated would be different on each delivery
    /// and would deduplicate nothing.
    /// </remarks>
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
    /// When the thing being notified about happened, as the publisher reported it.
    /// UTC.
    /// </summary>
    /// <remarks>
    /// Copied from the event rather than read from a clock here, for 021's reason:
    /// this module records an answer another module gave, and a second reading
    /// would be a second authority over when the sale happened. It also makes the
    /// row honest about a delivery that was delayed by a backlog.
    /// </remarks>
    public DateTime OccurredAt { get; set; }

    /// <summary>When this row was written. UTC.</summary>
    /// <remarks>
    /// Kept apart from <see cref="OccurredAt"/> because the gap between them is
    /// the outbox's delivery latency, which is the one number this table can
    /// report and nothing else can.
    /// </remarks>
    public DateTime CreatedAt { get; set; }
}
