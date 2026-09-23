using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.Application;

/// <summary>
/// Creates an event's seats in one transaction. No lock, since the rows are new, and no
/// outcome enum, since nothing can refuse a new seat.
/// </summary>
public sealed class CreateSeatMapCommandHandler(ISeatRepository seats)
{
    /// <summary>
    /// The largest seat map one request may create.
    /// </summary>
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
