using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;

namespace Encore.Modules.Inventory.Ports;

/// <summary>
/// The durable store of seats, and the source of truth for seat state. Besides loading
/// and saving aggregates, it answers the few questions that span several seats.
/// </summary>
public interface ISeatRepository
{
    /// <summary>Loads a seat, or <see langword="null"/> if it does not exist.</summary>
    Task<Seat?> GetByIdAsync(Guid seatId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads several seats as the database has them now, discarding unsaved changes.
    /// Missing seats are absent from the result.
    /// </summary>
    Task<IReadOnlyList<Seat>> GetByIdsAsync(
        IReadOnlyCollection<Guid> seatIds,
        CancellationToken cancellationToken = default);

    /// <summary>Saves a seat as one write guarded by its concurrency token.</summary>
    /// <remarks>Commits the whole unit of work, as the batch overload does.</remarks>
    /// <exception cref="ConcurrentSeatModificationException">The seat changed since it was loaded.</exception>
    Task SaveAsync(Seat seat, CancellationToken cancellationToken = default);

    /// <summary>Saves several seats in one transaction: all of them, or none.</summary>
    /// <remarks>
    /// Commits the whole unit of work: any other seat changed through the same scope is written
    /// too, and <paramref name="seats"/> only names one to blame on a conflict. After a lost race,
    /// reload the seats rather than leave changes in memory for a later save to find.
    /// </remarks>
    /// <exception cref="ConcurrentSeatModificationException">One of the seats changed, so nothing was written.</exception>
    Task SaveAsync(IReadOnlyCollection<Seat> seats, CancellationToken cancellationToken = default);

    /// <summary>
    /// What a hold needs in one read: the seats it asks for, loaded as
    /// <see cref="GetByIdsAsync"/> loads them, and the ids of every seat at the event this
    /// client holds live as of <paramref name="utcNow"/>, for the per-client cap. Ids rather
    /// than a count, so a re-hold is not counted as new. The cap's other seats are only
    /// counted, never handed back to be changed.
    /// </summary>
    Task<SeatsForHold> GetForHoldAsync(
        IReadOnlyCollection<Guid> seatIds,
        Guid clientId,
        Guid eventId,
        DateTime utcNow,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Seats whose holds have lapsed, oldest first. Candidates only: the sweep hands each
    /// back to the aggregate, which decides.
    /// </summary>
    Task<IReadOnlyList<Guid>> FindExpiredHoldsAsync(
        DateTime utcNow,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Inserts new seats in one transaction: all of them, or none.</summary>
    Task AddRangeAsync(IReadOnlyCollection<Seat> seats, CancellationToken cancellationToken = default);
}
