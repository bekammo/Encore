using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.Application;

/// <summary>
/// Brings an event's seats into existence. The only production caller of
/// <see cref="Seat.Create"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>No lock here, and that is not an oversight.</b> The other three use cases
/// take one because they contend for a row that already exists; this one writes
/// rows that do not exist yet, so there is nothing to contend for and no
/// concurrency token to lose. Taking a lock anyway would be cargo-cult
/// symmetry.
/// </para>
/// <para>
/// <b>No outcome enum either.</b> There is no domain refusal on this path —
/// every seat is born <c>Available</c> and nothing can object. The only way to
/// fail is to ask for a nonsense number, which is a malformed request rather
/// than a refusal, so it throws rather than returning a result the caller would
/// have to branch on.
/// </para>
/// </remarks>
public sealed class CreateSeatMapCommandHandler(ISeatRepository seats)
{
    /// <summary>
    /// The largest seat map one request may create.
    /// </summary>
    /// <remarks>
    /// Comfortably above a real arena, and low enough that a typo cannot ask the
    /// database for a million rows in one transaction. A venue needing more than
    /// this can be built from several calls.
    /// </remarks>
    public const int MaxSeatsPerRequest = 10_000;

    private readonly ISeatRepository _seats = seats;

    /// <summary>Creates <see cref="CreateSeatMapCommand.Count"/> available seats.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The count is not between 1 and <see cref="MaxSeatsPerRequest"/>.
    /// </exception>
    public async Task<CreateSeatMapResult> HandleAsync(
        CreateSeatMapCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(command.Count, 1, nameof(command));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(command.Count, MaxSeatsPerRequest, nameof(command));

        var created = new List<Seat>(command.Count);
        var seatIds = new List<Guid>(command.Count);

        for (var i = 0; i < command.Count; i++)
        {
            var seatId = Guid.NewGuid();

            created.Add(Seat.Create(seatId, command.EventId));
            seatIds.Add(seatId);
        }

        await _seats.AddRangeAsync(created, cancellationToken).ConfigureAwait(false);

        return new CreateSeatMapResult(seatIds);
    }
}
