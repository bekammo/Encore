using Encore.Modules.Inventory.Domain.Events;
using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Shared;

namespace Encore.Modules.Inventory.Domain;

/// <summary>
/// The only consistency boundary (003). A hold is the <see cref="HeldByClientId"/> /
/// <see cref="HoldExpiresAt"/> pair on this row, not an entity.
/// </summary>
public sealed class Seat
{
    /// <summary>Owned here: callers pass <c>utcNow</c>, never an expiry (003).</summary>
    public static readonly TimeSpan HoldDuration = TimeSpan.FromMinutes(5);

    private readonly List<IDomainEvent> _domainEvents = [];

    // EF Core materialisation only.
    private Seat()
    {
    }

    private Seat(Guid id, Guid eventId)
    {
        Id = id;
        EventId = eventId;
        Status = SeatStatus.Available;
    }

    public static Seat Create(Guid id, Guid eventId)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A seat's id must not be empty.", nameof(id));
        }

        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("A seat must belong to an event.", nameof(eventId));
        }

        return new Seat(id, eventId);
    }

    public Guid Id { get; private set; }

    public Guid EventId { get; private set; }

    /// <summary>
    /// A <see cref="SeatStatus.Held"/> row at or past <see cref="HoldExpiresAt"/> is available;
    /// nothing may wait for the sweep to fix the column (006).
    /// </summary>
    public SeatStatus Status { get; private set; }

    /// <summary>The holder, and after a sale the buyer.</summary>
    public Guid? HeldByClientId { get; private set; }

    public DateTime? HoldExpiresAt { get; private set; }

    /// <summary>The Postgres <c>xmin</c> system column (004).</summary>
    public uint RowVersion { get; private set; }

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    public void ClearDomainEvents() => _domainEvents.Clear();

    /// <summary>
    /// From available, or from a lapsed hold, which is reclaimed. Re-holding your own live hold
    /// is a no-op and does not extend it.
    /// </summary>
    /// <exception cref="SeatTransitionException">The seat is sold, or held by someone else.</exception>
    public void Hold(Guid clientId, DateTime utcNow)
    {
        GuardUtc(utcNow);
        GuardNotSold();

        if (EffectiveStatusAt(utcNow) is SeatStatus.Held)
        {
            if (HeldByClientId == clientId)
            {
                return;
            }

            throw new SeatTransitionException(Id, SeatTransitionReason.SeatAlreadyHeld);
        }

        // A reclaim records SeatReleased(Expired) before SeatHeld (003).
        ExpireHold(utcNow);

        var expiresAt = utcNow + HoldDuration;

        Status = SeatStatus.Held;
        HeldByClientId = clientId;
        HoldExpiresAt = expiresAt;

        Raise(new SeatHeld(Id, EventId, clientId, expiresAt, utcNow));
    }

    /// <summary>
    /// The sweep's transition, and <see cref="Hold"/>'s reclaim. Only the status changes: the
    /// lapsed holder pair stays, so a swept seat refuses a sale exactly as an unswept one (023).
    /// </summary>
    public bool ExpireHold(DateTime utcNow)
    {
        GuardUtc(utcNow);

        if (Status is not SeatStatus.Held || EffectiveStatusAt(utcNow) is not SeatStatus.Available)
        {
            return false;
        }

        Status = SeatStatus.Available;

        if (HeldByClientId is { } holder)
        {
            Raise(new SeatReleased(Id, EventId, holder, SeatReleaseReason.Expired, utcNow));
        }

        return true;
    }

    /// <summary>Releasing an available seat, or a lapsed hold, is a no-op.</summary>
    /// <exception cref="SeatTransitionException">The seat is sold, or held by someone else.</exception>
    public void Release(Guid clientId, DateTime utcNow)
    {
        GuardUtc(utcNow);
        GuardNotSold();

        if (EffectiveStatusAt(utcNow) is SeatStatus.Available)
        {
            return;
        }

        if (HeldByClientId != clientId)
        {
            throw new SeatTransitionException(Id, SeatTransitionReason.NotTheHolder);
        }

        Status = SeatStatus.Available;
        HeldByClientId = null;
        HoldExpiresAt = null;

        Raise(new SeatReleased(Id, EventId, clientId, SeatReleaseReason.Cancelled, utcNow));
    }

    /// <summary>
    /// Needs this client's live hold: there is no route from available straight to sold.
    /// </summary>
    /// <exception cref="SeatTransitionException">
    /// The seat is sold, has no live hold, or is held by someone else.
    /// </exception>
    public void Sell(Guid clientId, DateTime utcNow)
    {
        GuardUtc(utcNow);
        GuardNotSold();

        if (EffectiveStatusAt(utcNow) is SeatStatus.Available)
        {
            var reason = (HoldExpiresAt is not null, HeldByClientId == clientId) switch
            {
                (true, true) => SeatTransitionReason.HoldExpired,
                (true, false) => SeatTransitionReason.NotTheHolder,
                _ => SeatTransitionReason.NoActiveHold
            };

            throw new SeatTransitionException(Id, reason);
        }

        if (HeldByClientId != clientId)
        {
            throw new SeatTransitionException(Id, SeatTransitionReason.NotTheHolder);
        }

        Status = SeatStatus.Sold;
        HoldExpiresAt = null;

        Raise(new SeatSold(Id, EventId, clientId, utcNow));
    }

    private SeatStatus EffectiveStatusAt(DateTime utcNow)
        => Status is SeatStatus.Held && HoldExpiresAt <= utcNow
            ? SeatStatus.Available
            : Status;

    private static void GuardUtc(DateTime utcNow)
    {
        if (utcNow.Kind is not DateTimeKind.Utc)
        {
            throw new ArgumentException(
                $"utcNow must be a UTC instant; its Kind was {utcNow.Kind}. Time enters this system once, through TimeProvider.GetUtcNow().UtcDateTime.",
                nameof(utcNow));
        }
    }

    private void GuardNotSold()
    {
        if (Status is SeatStatus.Sold)
        {
            throw new SeatTransitionException(Id, SeatTransitionReason.SeatAlreadySold);
        }
    }

    private void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);
}
