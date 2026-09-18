namespace Encore.Modules.Inventory.Application;

/// <summary>
/// A request to put one seat in one client's basket.
/// </summary>
/// <remarks>
/// <para>
/// Carries no expiry and no timestamp. How long a hold lasts is the aggregate's
/// rule, and what "now" is comes from the handler's clock — a caller that could
/// supply either would be able to grant itself a hold the rules never approved.
/// </para>
/// <para>
/// It does carry <see cref="EventId"/>, which is not needed to identify the seat.
/// The hold cap is scoped per client per event (<c>DECISIONS.md</c> 006), so the
/// handler needs the event to count against and to key its lock on, and taking it
/// from the caller avoids reading the seat before the locks are held. It is
/// checked against the seat rather than trusted.
/// </para>
/// </remarks>
/// <param name="EventId">The event the seat belongs to.</param>
/// <param name="SeatId">The seat being claimed.</param>
/// <param name="ClientId">Who is claiming it.</param>
public sealed record HoldSeatCommand(Guid EventId, Guid SeatId, Guid ClientId);
