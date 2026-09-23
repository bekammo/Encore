using Encore.Modules.Inventory.Domain.Events;
using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Shared;

namespace Encore.Modules.Inventory.Domain;

/// <summary>
/// Aggregate root for one seat at one event, and the only consistency boundary.
/// A hold is not an entity: it is the <see cref="HeldByClientId"/> /
/// <see cref="HoldExpiresAt"/> pair on this row.
/// </summary>
/// <remarks>
/// Races are settled by optimistic concurrency on <see cref="RowVersion"/>. Time is
/// always passed in, never read, so expiry is testable without waiting.
/// </remarks>
public sealed class Seat
{
    /// <summary>How long a hold lasts. Owned here so no caller can choose its own expiry.</summary>
    public static readonly TimeSpan HoldDuration = TimeSpan.FromMinutes(5);

    private readonly List<IDomainEvent> _domainEvents = [];

    /// <summary>For EF Core materialisation only.</summary>
    private Seat()
    {
    }

    private Seat(Guid id, Guid eventId)
    {
        Id = id;
        EventId = eventId;
        Status = SeatStatus.Available;
    }

    /// <summary>
    /// Creates a seat. Every seat starts <see cref="SeatStatus.Available"/>; the only
    /// routes to held or sold are the transition methods.
    /// </summary>
    /// <exception cref="ArgumentException">Either id is empty.</exception>
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
    /// Persisted status. A <see cref="SeatStatus.Held"/> row whose hold has lapsed is
    /// effectively available, so read it together with <see cref="HoldExpiresAt"/>.
    /// </summary>
    public SeatStatus Status { get; private set; }

    /// <summary>The holder while held, and the buyer once sold.</summary>
    public Guid? HeldByClientId { get; private set; }

    /// <summary>UTC instant at which the current hold lapses.</summary>
    public DateTime? HoldExpiresAt { get; private set; }

    /// <summary>Concurrency token, mapped to the Postgres <c>xmin</c> system column.</summary>
    public uint RowVersion { get; private set; }

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    /// <summary>
    /// Drops recorded events. Handlers call it before an attempt; the outbox drain
    /// calls it after a successful save so the events are not written twice.
    /// </summary>
    public void ClearDomainEvents() => _domainEvents.Clear();

    /// <summary>
    /// Holds the seat for <see cref="HoldDuration"/>. Allowed from available, or from a
    /// lapsed hold, which is reclaimed. Re-holding your own live hold is a no-op.
    /// </summary>
    /// <exception cref="SeatTransitionException">The seat is sold, or held by someone else.</exception>
    public void Hold(Guid clientId, DateTime utcNow)
    {
        GuardUtc(utcNow);
        GuardNotSold();

        if (EffectiveStatusAt(utcNow) is SeatStatus.Held)
        {
            // Idempotent for the holder, and the expiry does not move: repeating the
            // request must not extend the hold.
            if (HeldByClientId == clientId)
            {
                return;
            }

            throw new SeatTransitionException(Id, SeatTransitionReason.SeatAlreadyHeld);
        }

        // Ends a lapsed hold first, so its SeatReleased is recorded before the new SeatHeld.
        ExpireHold(utcNow);

        var expiresAt = utcNow + HoldDuration;

        Status = SeatStatus.Held;
        HeldByClientId = clientId;
        HoldExpiresAt = expiresAt;

        Raise(new SeatHeld(Id, EventId, clientId, expiresAt, utcNow));
    }

    /// <summary>
    /// Ends a hold that has already lapsed and records it as expired. Used by
    /// <see cref="Hold"/> when reclaiming and by the background sweep.
    /// </summary>
    /// <returns>Whether a lapsed hold was ended. A live hold or a sold seat is left alone.</returns>
    public bool ExpireHold(DateTime utcNow)
    {
        GuardUtc(utcNow);

        if (Status is not SeatStatus.Held || EffectiveStatusAt(utcNow) is not SeatStatus.Available)
        {
            return false;
        }

        var lapsedHolder = HeldByClientId;

        Status = SeatStatus.Available;
        HeldByClientId = null;
        HoldExpiresAt = null;

        if (lapsedHolder is { } holder)
        {
            Raise(new SeatReleased(Id, EventId, holder, SeatReleaseReason.Expired, utcNow));
        }

        return true;
    }

    /// <summary>
    /// Releases the holder's seat. A no-op if the seat is already effectively available,
    /// so a retry is not an error.
    /// </summary>
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
    /// Converts this client's live hold into a sale. Terminal. There is no route from
    /// available straight to sold.
    /// </summary>
    /// <exception cref="SeatTransitionException">No live hold, or not this client's.</exception>
    public void Sell(Guid clientId, DateTime utcNow)
    {
        GuardUtc(utcNow);
        GuardNotSold();

        if (EffectiveStatusAt(utcNow) is SeatStatus.Available)
        {
            // "Your hold ran out" and "you never held this" are different answers.
            var reason = (Status, HeldByClientId == clientId) switch
            {
                (SeatStatus.Held, true) => SeatTransitionReason.HoldExpired,
                (SeatStatus.Held, false) => SeatTransitionReason.NotTheHolder,
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

        // HeldByClientId is kept, so the row still says who owns the seat.
        Raise(new SeatSold(Id, EventId, clientId, utcNow));
    }

    /// <summary>The status as of <paramref name="utcNow"/>, with a lapsed hold reported as available.</summary>
    private SeatStatus EffectiveStatusAt(DateTime utcNow)
        => Status is SeatStatus.Held && HoldExpiresAt <= utcNow
            ? SeatStatus.Available
            : Status;

    /// <summary>Every transition requires a UTC instant; <c>Local</c> and <c>Unspecified</c> are refused.</summary>
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
