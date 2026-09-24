using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;

namespace Encore.Modules.Inventory.Ports;

public interface ISeatRepository
{
    Task<Seat?> GetByIdAsync(Guid seatId, CancellationToken cancellationToken = default);

    /// <summary>Loads the seats as the database has them now, discarding unsaved changes.</summary>
    Task<IReadOnlyList<Seat>> GetByIdsAsync(
        IReadOnlyCollection<Guid> seatIds,
        CancellationToken cancellationToken = default);

    /// <remarks>Commits the whole unit of work, as the batch overload does.</remarks>
    /// <exception cref="ConcurrentSeatModificationException">The seat changed since it was loaded.</exception>
    Task SaveAsync(Seat seat, CancellationToken cancellationToken = default);

    /// <summary>One transaction: all of them, or none (011).</summary>
    /// <remarks>
    /// Commits the whole unit of work: any other seat changed through the same scope is written
    /// too, and <paramref name="seats"/> only names one to blame on a conflict. After a lost race,
    /// reload the seats rather than leave changes in memory for a later save to find.
    /// </remarks>
    /// <exception cref="ConcurrentSeatModificationException">One of the seats changed, so nothing was written.</exception>
    Task SaveAsync(IReadOnlyCollection<Seat> seats, CancellationToken cancellationToken = default);

    /// <summary>
    /// The requested seats, loaded as <see cref="GetByIdsAsync"/> loads them, and the ids of every
    /// seat at the event the client holds live, requested or not. Ids rather than a count, so a
    /// re-hold is not counted as new.
    /// </summary>
    Task<SeatsForHold> GetForHoldAsync(
        IReadOnlyCollection<Guid> seatIds,
        Guid clientId,
        Guid eventId,
        DateTime utcNow,
        CancellationToken cancellationToken = default);

    /// <summary>Candidates only, oldest first: the aggregate decides each one (006).</summary>
    Task<IReadOnlyList<Guid>> FindExpiredHoldsAsync(
        DateTime utcNow,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>One transaction: all of them, or none.</summary>
    Task AddRangeAsync(IReadOnlyCollection<Seat> seats, CancellationToken cancellationToken = default);
}
