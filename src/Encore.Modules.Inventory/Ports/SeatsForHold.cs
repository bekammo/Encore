using Encore.Modules.Inventory.Domain;

namespace Encore.Modules.Inventory.Ports;

/// <summary>What <see cref="ISeatRepository.GetForHoldAsync"/> answers.</summary>
/// <param name="Seats">The requested seats that exist. Missing ones are absent.</param>
/// <param name="LiveHolds">Every seat at the event the client holds live, requested or not.</param>
public sealed record SeatsForHold(IReadOnlyList<Seat> Seats, IReadOnlyCollection<Guid> LiveHolds);
