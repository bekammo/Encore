using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;

namespace Encore.Modules.Inventory.Ports;

/// <summary>
/// The durable store of seats — Postgres, and the authoritative source of truth
/// for seat state. Two of these methods load an aggregate and write it back; the
/// third answers a question about seats that no single aggregate can.
/// </summary>
/// <remarks>
/// <see cref="CountLiveHoldsAsync"/> is not aggregate access and sits slightly
/// awkwardly next to the other two. It lives here anyway rather than behind its
/// own port: a one-method interface with one implementation, never substituted,
/// is the ceremony <c>DECISIONS.md</c> 001 argues against paying for. What it
/// must not become is a general query surface — anything that grows past
/// "questions Postgres can answer about seats that a single <see cref="Seat"/>
/// cannot" belongs on a read-side port of its own.
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
}
