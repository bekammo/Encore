using Encore.Modules.Inventory.Domain.Events;
using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Shared;

namespace Encore.Modules.Inventory.Domain;

/// <summary>
/// Aggregate root for a single seat at a single event, and the sole consistency
/// boundary for everything that can happen to it. A hold is not a separate
/// entity: it is the <see cref="HeldByClientId"/> / <see cref="HoldExpiresAt"/>
/// pair on this row, so acquiring, losing or converting a hold is always a
/// single-row state change and never a multi-table dance.
/// </summary>
/// <remarks>
/// <para>
/// Atomicity comes from optimistic concurrency on <see cref="RowVersion"/>: a
/// transition is one conditional write that is rejected with
/// <see cref="ConcurrentSeatModificationException"/> if the row moved underneath
/// it. The Redis lock in front of this is a contention optimiser, not the
/// correctness mechanism — if it disappeared entirely, the invariants would
/// still hold.
/// </para>
/// <para>
/// Time is a parameter here, never a reading. Nothing in this class knows what
/// "now" is unless a caller says so, which is what makes "the hold lapsed one
/// minute ago" a microsecond-long unit test instead of a sleep.
/// </para>
/// </remarks>
public sealed class Seat
{
    /// <summary>
    /// How long a hold lasts. Owned by the aggregate rather than supplied by the
    /// caller: an expiry passed in from outside would let any caller grant itself
    /// a hold lasting until the year 3000, which is exactly the kind of state no
    /// rule ever approved.
    /// </summary>
    public static readonly TimeSpan HoldDuration = TimeSpan.FromMinutes(5);

    private readonly List<IDomainEvent> _domainEvents = [];

    /// <summary>
    /// For EF Core's materialisation pipeline only. Never call this from
    /// application code — it exists because the ORM needs a way in, not because
    /// there is a second way to bring a seat into being.
    /// </summary>
    /// <remarks>
    /// Private rather than protected because <see cref="Seat"/> is sealed; there
    /// is no derived type that could need it.
    /// </remarks>
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
    /// The only way application code can bring a seat into existence.
    /// </summary>
    /// <remarks>
    /// Deliberately takes no status, no holder and no expiry: every seat that
    /// exists was born <see cref="SeatStatus.Available"/>, and that is a fact the
    /// compiler enforces rather than a convention a caller has to remember. A
    /// seat cannot be conjured directly into <see cref="SeatStatus.Held"/> or
    /// <see cref="SeatStatus.Sold"/> by a test, a seed script or a tired
    /// teammate — the only routes to those states are the transition methods,
    /// which is what makes the rules they enforce unbypassable.
    /// </remarks>
    /// <param name="id">Identity for the new seat, assigned by the caller.</param>
    /// <param name="eventId">The concert this seat belongs to.</param>
    /// <exception cref="ArgumentException">Either id is empty.</exception>
    public static Seat Create(Guid id, Guid eventId)
    {
        // DECISIONS 038, answering the open half of 005's note. An empty Guid is
        // not an identity: a seat with one cannot be addressed and collides on
        // the primary key with the next one, and a seat belonging to event
        // Guid.Empty belongs to no event. Both are states no rule approved, which
        // is the hole this factory exists to close. The rest of the system
        // already agrees — every ClientIdEndpointFilter refuses an empty client
        // id at the edge.
        //
        // ArgumentException rather than SeatTransitionException: that exception
        // carries a closed reason enum which SeatResults switches over
        // exhaustively, and a malformed construction has no HTTP request that can
        // produce it. 012 set the precedent with a nonsense seat count.
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

    /// <summary>Identity of this seat. Stable for the life of the event.</summary>
    public Guid Id { get; private set; }

    /// <summary>The concert this seat belongs to.</summary>
    public Guid EventId { get; private set; }

    /// <summary>
    /// Persisted status. Read it together with <see cref="HoldExpiresAt"/> —
    /// see <see cref="SeatStatus"/> for why this column alone is not the truth.
    /// </summary>
    public SeatStatus Status { get; private set; }

    /// <summary>
    /// Who holds the seat while <see cref="Status"/> is <see cref="SeatStatus.Held"/>,
    /// and who bought it once it is <see cref="SeatStatus.Sold"/>.
    /// </summary>
    public Guid? HeldByClientId { get; private set; }

    /// <summary>UTC instant at which the current hold lapses, if there is one.</summary>
    public DateTime? HoldExpiresAt { get; private set; }

    /// <summary>
    /// Concurrency token. Mapped to the Postgres <c>xmin</c> system column, so
    /// the database maintains it and no code here has to remember to.
    /// </summary>
    public uint RowVersion { get; private set; }

    /// <summary>Events raised by transitions on this aggregate, in order.</summary>
    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    /// <summary>
    /// Drops the recorded events. Every caller today is an application handler
    /// scrubbing a rejected attempt before it retries — not a drain, because
    /// nothing reads <see cref="DomainEvents"/> yet. Whether the outbox drain
    /// also calls this when it arrives is open; see <c>DECISIONS.md</c> 008 and 044.
    /// </summary>
    public void ClearDomainEvents() => _domainEvents.Clear();

    /// <summary>
    /// Claims the seat for a client until <see cref="HoldDuration"/> from now.
    /// </summary>
    /// <remarks>
    /// Permitted from <see cref="SeatStatus.Available"/>, and from
    /// <see cref="SeatStatus.Held"/> when that hold has lapsed — the lazy half of
    /// expiry enforcement. Refusing a lapsed hold would make correctness depend on
    /// the background sweep having run, which the design forbids.
    /// </remarks>
    /// <exception cref="SeatTransitionException">The seat is sold, or held by someone else.</exception>
    public void Hold(Guid clientId, DateTime utcNow)
    {
        GuardUtc(utcNow);
        GuardNotSold();

        if (EffectiveStatusAt(utcNow) is SeatStatus.Held)
        {
            // A live hold. If it is already this client's, the postcondition holds
            // and there is nothing to do — re-holding is idempotent so that a
            // retried request does not report a seat as taken to the client that
            // actually has it. The expiry deliberately does not move; extending a
            // hold by repeating the request would let a client squat indefinitely.
            if (HeldByClientId == clientId)
            {
                return;
            }

            throw new SeatTransitionException(Id, SeatTransitionReason.SeatAlreadyHeld);
        }

        // Effectively available. If the row still reads Held, a hold lapsed and
        // this is the moment it is reclaimed; record that the old hold ended
        // before recording the new one, or the log shows two consecutive claims
        // with no way to tell when the first stopped being true.
        if (Status is SeatStatus.Held && HeldByClientId is { } lapsedHolder)
        {
            Raise(new SeatReleased(Id, EventId, lapsedHolder, SeatReleaseReason.Expired, utcNow));
        }

        var expiresAt = utcNow + HoldDuration;

        Status = SeatStatus.Held;
        HeldByClientId = clientId;
        HoldExpiresAt = expiresAt;

        Raise(new SeatHeld(Id, EventId, clientId, expiresAt, utcNow));
    }

    /// <summary>
    /// Returns a held seat to the available pool at the holding client's request.
    /// </summary>
    /// <remarks>
    /// A no-op when the seat is already effectively available, including when the
    /// caller's own hold has quietly lapsed: the caller wanted to not be holding
    /// this seat, and they are not. Refusing would turn a retry into an error.
    /// </remarks>
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
    /// Converts this client's live hold into a confirmed sale. Terminal.
    /// </summary>
    /// <remarks>
    /// Requires a hold, and requires it to be this client's and unexpired. There is
    /// no direct Available-to-Sold route: allowing one would make the hold step
    /// skippable, and a seat sold out from under an actively-paying holder is a
    /// double-sell that optimistic concurrency cannot catch, because the two
    /// writers never race on the same row version.
    /// </remarks>
    /// <exception cref="SeatTransitionException">No live hold, or not this client's.</exception>
    public void Sell(Guid clientId, DateTime utcNow)
    {
        GuardUtc(utcNow);
        GuardNotSold();

        if (EffectiveStatusAt(utcNow) is SeatStatus.Available)
        {
            // Same refusal, but "your hold ran out" and "you never held this" are
            // very different things to tell a customer who is mid-checkout — so
            // the reason turns on who is asking, not merely on what the row says.
            // Deciding it from Status alone told a client whose hold had lapsed
            // apart from a client who never had one, which is the distinction
            // that matters least; both look identical to the row.
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

        // HeldByClientId is deliberately kept: the claim is now permanent, and the
        // row should be able to answer "who owns this seat" without replaying events.
        Raise(new SeatSold(Id, EventId, clientId, utcNow));
    }

    /// <summary>
    /// The seat's status as of <paramref name="utcNow"/>, with a lapsed hold
    /// reported as <see cref="SeatStatus.Available"/> whatever the column says.
    /// </summary>
    /// <remarks>
    /// Private because every path that needs it is in this class. If a read model
    /// ever needs to show effective availability, this becomes public rather than
    /// being reimplemented anywhere else — a second copy of this rule is how the
    /// sweep would quietly become load-bearing.
    /// </remarks>
    private SeatStatus EffectiveStatusAt(DateTime utcNow)
        => Status is SeatStatus.Held && HoldExpiresAt <= utcNow
            ? SeatStatus.Available
            : Status;

    /// <summary>
    /// Every transition takes the current instant as a parameter, and that
    /// instant must be UTC. This is where that contract is checked rather than
    /// assumed. See <c>DECISIONS.md</c> 039.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Here rather than at the handler boundary.</b> The three handlers get
    /// their instant from <c>TimeProvider.GetUtcNow().UtcDateTime</c>, which
    /// cannot return anything else, so a check there would be tautological where
    /// it sits and would protect nothing from the aggregate's other callers —
    /// tests, a future bulk import, the sweep when it arrives. The mistake this
    /// guards against arrives at the aggregate, so the precondition belongs on
    /// the methods whose contract it is.
    /// </para>
    /// <para>
    /// <b><see cref="DateTimeKind.Unspecified"/> is refused alongside
    /// <see cref="DateTimeKind.Local"/>.</b> A wall clock with no zone is a
    /// different instant in London and in Los Angeles, so quietly reading it as
    /// UTC would be a guess wearing the costume of a conversion.
    /// </para>
    /// <para>
    /// This is what makes every <c>IDomainEvent.OccurredAt</c> UTC by
    /// construction: the events are built from this parameter, and
    /// <see cref="HoldExpiresAt"/> is derived from it, so nothing downstream
    /// needs a check of its own.
    /// </para>
    /// <para>
    /// <see cref="ArgumentException"/>, not <see cref="SeatTransitionException"/>:
    /// a non-UTC instant is a bug in the caller, not a refusal about the state of
    /// the world, and the reason enum is a closed set with an HTTP mapping behind
    /// every member.
    /// </para>
    /// </remarks>
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
