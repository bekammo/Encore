namespace Encore.Modules.Orders.Endpoints;

/// <summary>Body of a request to start a checkout.</summary>
/// <param name="EventId">The event being bought into. One event per order.</param>
/// <param name="SeatIds">At least one, at most the published hold cap, no duplicates.</param>
public sealed record CheckoutRequest(Guid EventId, IReadOnlyList<Guid> SeatIds);
