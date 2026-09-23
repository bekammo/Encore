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
    /// <exception cref="ConcurrentSeatModificationException">The seat changed since it was loaded.</exception>
    Task SaveAsync(Seat seat, CancellationToken cancellationToken = default);

    /// <summary>Saves several seats in one transaction: all of them, or none.</summary>
    /// <exception cref="ConcurrentSeatModificationException">One of the seats changed, so nothing was written.</exception>
    Task SaveAsync(IReadOnlyCollection<Seat> seats, CancellationToken cancellationToken = default);

    /// <summary>
    /// The seats at one event a client holds live as of <paramref name="utcNow"/>, for the
    /// per-client hold cap. Ids rather than a count, so a re-hold is not counted as new.
    /// </summary>
    Task<IReadOnlyCollection<Guid>> FindLiveHoldsAsync(
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
