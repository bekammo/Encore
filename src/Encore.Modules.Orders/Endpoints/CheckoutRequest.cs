namespace Encore.Modules.Orders.Endpoints;

/// <summary>Body of a request to start a checkout.</summary>
/// <param name="EventId">The event being bought into. One event per order.</param>
/// <param name="SeatIds">
/// The seats to buy. At least one, at most the published hold cap, and no
/// duplicates — a repeated id is refused rather than collapsed, because a seat
/// is a thing you can buy exactly one of and guessing which was meant is how
/// you stop finding out.
/// </param>
public sealed record CheckoutRequest(Guid EventId, IReadOnlyList<Guid> SeatIds);
