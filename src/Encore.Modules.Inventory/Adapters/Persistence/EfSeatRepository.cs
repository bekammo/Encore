using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Domain.Exceptions;
using Encore.Modules.Inventory.Ports;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Inventory.Adapters.Persistence;

/// <summary>
/// EF Core / Postgres implementation of <see cref="ISeatRepository"/>. This is
/// where the concurrency guarantee the port promises actually gets paid for —
/// the <c>xmin</c> token on the seat row turns every save into a conditional
/// UPDATE, and the loser of a race finds out here.
/// </summary>
/// <remarks>
/// The one thing this adapter must not do is let EF Core's vocabulary escape
/// through the port, so <see cref="DbUpdateConcurrencyException"/> is caught and
/// translated into <see cref="ConcurrentSeatModificationException"/>. The
/// original is kept as the inner exception: callers get a contract they can
/// depend on, diagnostics keep the detail.
/// </remarks>
public sealed class EfSeatRepository(InventoryDbContext context) : ISeatRepository
{
    private readonly InventoryDbContext _context = context;

    /// <inheritdoc />
    public Task<Seat?> GetByIdAsync(Guid seatId, CancellationToken cancellationToken = default)
        => _context.Seats.SingleOrDefaultAsync(seat => seat.Id == seatId, cancellationToken);

    /// <inheritdoc />
    public async Task SaveAsync(Seat seat, CancellationToken cancellationToken = default)
    {
        // TODO: drain seat.DomainEvents into the outbox in this same transaction,
        // then ClearDomainEvents(). Waiting on the outbox table (Soundcheck).
        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrentSeatModificationException(seat.Id, ex);
        }
    }
}
