using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;

namespace Encore.Modules.Inventory.Ports;

/// <summary>
/// The durable store of seats — Postgres, and the authoritative source of truth
/// for seat state. Two of these methods load an aggregate and write it back; the
/// others answer questions about seats that no single aggregate can.
/// </summary>
/// <remarks>
/// <see cref="CountLiveHoldsAsync"/> and <see cref="FindExpiredHoldsAsync"/> are
/// not aggregate access and sit slightly awkwardly next to the rest. They live
/// here anyway rather than behind ports of their own: a one-method interface with
/// one implementation, never substituted, is the ceremony <c>DECISIONS.md</c> 001
/// argues against paying for. What this must not become is a general query
/// surface — anything that grows past "questions Postgres can answer about seats
/// that a single <see cref="Seat"/> cannot" belongs on a read-side port of its
/// own.
/// </remarks>
public interface ISeatRepository
{
    /// <summary>Loads a seat, or <see langword="null"/> if no such seat exists.</summary>
    Task<Seat?> GetByIdAsync(Guid seatId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a seat's current state as a single conditional write guarded by
    /// its concurrency token.
    /// </summary>
    /// <exception cref="ConcurrentSeatModificationException">
    /// The seat changed underneath this instance since it was loaded. The caller
    /// lost the race and should reload rather than retry blindly. Adapters
    /// translate their own storage failure into this — the contract names no
    /// persistence library, so callers never couple to one.
    /// </exception>
    Task SaveAsync(Seat seat, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the seats at one event that a client is holding live as of
    /// <paramref name="utcNow"/>, ignoring one seat.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Backs the per-client hold cap (<c>DECISIONS.md</c> 006), which spans
    /// several rows and so cannot live in <see cref="Seat"/>.
    /// </para>
    /// <para>
    /// "Live" applies the same lazy-expiry rule the aggregate does: a row still
    /// reading <c>Held</c> whose hold has lapsed does not count, because it is
    /// logically available whatever the column says. Counting in Postgres rather
    /// than tracking a tally elsewhere is what makes that automatic — there is no
    /// second copy of the expiry rule to drift, and no bookkeeping to get wrong.
    /// </para>
    /// </remarks>
    /// <param name="clientId">The client whose holds are counted.</param>
    /// <param name="eventId">The event to count within. The cap is per event.</param>
    /// <param name="excludingSeatId">
    /// The seat being requested, which is left out of the count. Without this, a
    /// client at the cap could not re-send a request for a seat they already
    /// hold — the count would include it and refuse them their own seat, on
    /// exactly the duplicate-request path the idempotent re-hold exists to
    /// protect. The question is therefore "how many *other* seats", not "how
    /// many seats".
    /// </param>
    /// <param name="utcNow">The instant to judge expiry against.</param>
    Task<int> CountLiveHoldsAsync(
        Guid clientId,
        Guid eventId,
        Guid excludingSeatId,
        DateTime utcNow,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The seats whose holds have lapsed as of <paramref name="utcNow"/>, oldest
    /// lapse first, capped at <paramref name="limit"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Backs the expired-hold sweep (<c>DECISIONS.md</c> 062), and like
    /// <see cref="CountLiveHoldsAsync"/> it is a question about seats that no
    /// single <see cref="Seat"/> can answer.
    /// </para>
    /// <para>
    /// <b>It returns candidates, not verdicts, and the distinction is the whole
    /// reason the sweep stays cleanup.</b> The rows it names may have been sold or
    /// re-held by the time the caller loads them, and the caller is expected to
    /// hand each one back to the aggregate rather than act on this list. 007
    /// forbids a second copy of the lapsed-hold rule anywhere; this is the same
    /// predicate expressed in SQL for selectivity, and it is deliberately given no
    /// authority over what happens next.
    /// </para>
    /// <para>
    /// Ids only. The sweep visits each seat in a scope of its own, so materialising
    /// aggregates here would be loading them on a context that will not save them.
    /// </para>
    /// </remarks>
    /// <param name="utcNow">The instant to judge the lapse against.</param>
    /// <param name="limit">Ceiling on how many ids come back.</param>
    Task<IReadOnlyList<Guid>> FindExpiredHoldsAsync(
        DateTime utcNow,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a batch of new seats as one transaction — all of them, or none.
    /// </summary>
    /// <remarks>
    /// A seat map is one act, not N acts: half a venue is not a smaller venue,
    /// it is a broken one that somebody then has to reconcile by hand.
    /// <para>
    /// Deliberately does not throw <see cref="ConcurrentSeatModificationException"/>.
    /// These rows do not exist yet, so there is no concurrency token to lose and
    /// nothing to race against. A duplicate id would be a key violation, which is
    /// a caller bug rather than a lost race, and is left to propagate.
    /// </para>
    /// </remarks>
    Task AddRangeAsync(IReadOnlyCollection<Seat> seats, CancellationToken cancellationToken = default);
}
