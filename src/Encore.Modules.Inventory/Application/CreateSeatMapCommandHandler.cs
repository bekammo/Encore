using Encore.Modules.Inventory.Domain;
using Encore.Modules.Inventory.Ports;

namespace Encore.Modules.Inventory.Application;

public sealed class CreateSeatMapCommandHandler(ISeatRepository seats)
{
    public const int MaxSeatsPerRequest = 10_000;

    private readonly ISeatRepository _seats = seats;

    public async Task<IReadOnlyList<Guid>> HandleAsync(
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

        return seatIds;
    }
}
