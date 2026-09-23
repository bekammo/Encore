using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;

namespace Encore.Modules.Inventory.Ports;

/// <summary>
/// The durable store of seats — Postgres, and the authoritative source of truth
/// for seat state. Two of these methods load an aggregate and write it back; the
/// others answer questions about seats that no single aggregate can.
/// </summary>
/// <remarks>
/// <see cref="FindLiveHoldsAsync"/> and <see cref="FindExpiredHoldsAsync"/> are
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
    /// Loads several seats in one read, as the database has them now. Seats that
    /// do not exist are simply absent from the result, which is in no particular
    /// order.
    /// </summary>
    /// <remarks>
    /// Any unsaved change to one of these seats is discarded, for the reason
    /// <see cref="GetByIdAsync"/> defeats the identity map (009): a caller
    /// reloading after a lost race or a refused batch must see the row, not the
    /// attempt that failed.
    /// </remarks>
    Task<IReadOnlyList<Seat>> GetByIdsAsync(
        IReadOnlyCollection<Guid> seatIds,
        CancellationToken cancellationToken = default);

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
    /// Persists several seats in one transaction: every conditional write lands,
    /// or none does.
    /// </summary>
    /// <remarks>
    /// This is what lets a confirm sell an order's seats together (076). Each
    /// seat still enforces its own rules; the transaction adds atomicity across
    /// them and no rule of its own.
    /// </remarks>
    /// <exception cref="ConcurrentSeatModificationException">
    /// One of the seats changed underneath this instance, so nothing was written.
    /// </exception>
    Task SaveAsync(IReadOnlyCollection<Seat> seats, CancellationToken cancellationToken = default);

    /// <summary>
    /// The seats at one event that a client is holding live as of
    /// <paramref name="utcNow"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Backs the per-client hold cap (<c>DECISIONS.md</c> 006), which spans
    /// several rows and so cannot live in <see cref="Seat"/>.
    /// </para>
    /// <para>
    /// Ids rather than a count, because a batch has to tell a seat the client
    /// already holds — re-holding it is free and must not be refused at the cap —
    /// from a seat that would be a new hold. A count that left the requested seats
    /// out could not say which of them were already this client's (076).
    /// </para>
    /// <para>
    /// "Live" applies the same lazy-expiry rule the aggregate does: a row still
    /// reading <c>Held</c> whose hold has lapsed does not count, because it is
    /// logically available whatever the column says. Asking Postgres rather than
    /// tracking a tally elsewhere is what makes that automatic — there is no
    /// second copy of the expiry rule to drift, and no bookkeeping to get wrong.
    /// </para>
    /// </remarks>
    /// <param name="clientId">The client whose holds are wanted.</param>
    /// <param name="eventId">The event to look within. The cap is per event.</param>
    /// <param name="utcNow">The instant to judge expiry against.</param>
    Task<IReadOnlyCollection<Guid>> FindLiveHoldsAsync(
        Guid clientId,
        Guid eventId,
        DateTime utcNow,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The seats whose holds have lapsed as of <paramref name="utcNow"/>, oldest
    /// lapse first, capped at <paramref name="limit"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Backs the expired-hold sweep (<c>DECISIONS.md</c> 062), and like
    /// <see cref="FindLiveHoldsAsync"/> it is a question about seats that no
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
