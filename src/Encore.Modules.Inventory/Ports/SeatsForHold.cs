using Encore.Modules.Inventory.Domain;

namespace Encore.Modules.Inventory.Ports;

public sealed record SeatsForHold(IReadOnlyList<Seat> Seats, IReadOnlyCollection<Guid> LiveHolds);
