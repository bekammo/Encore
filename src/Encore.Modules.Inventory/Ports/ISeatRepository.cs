using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;

namespace Encore.Modules.Inventory.Ports;

/// <summary>
/// The durable store of seats — Postgres, and the authoritative source of truth
/// for seat state. Deliberately two methods: load an aggregate, write it back.
/// </summary>
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
}
