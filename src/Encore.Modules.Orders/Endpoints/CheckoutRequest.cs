namespace Encore.Modules.Orders.Endpoints;

public sealed record CheckoutRequest(Guid EventId, IReadOnlyList<Guid> SeatIds);
