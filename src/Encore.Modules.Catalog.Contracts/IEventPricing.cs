namespace Encore.Modules.Catalog.Contracts;

/// <summary>
/// What another module may ask Catalog about an event before selling it.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is named for what the caller needs, not for what it is attached to.</b>
/// An <c>ICatalogQueries</c> would invite every future question about the
/// catalogue to land here, and inside a year it would be a second read model
/// with no owner. <c>IEventPricing</c> can only grow in one direction, and
/// anything that is not "what does it cost to sell a seat at this event, and may
/// I yet" belongs on a contract of its own. <c>ISeatRepository</c> carries the
/// same warning about <c>CountLiveHoldsAsync</c>, for the same reason.
/// </para>
/// <para>
/// <b>Refusals are return values.</b> An event that does not exist is an
/// ordinary answer, reported through <see cref="EventPricingStatus"/>; an
/// exception from here means something genuinely broke. This is 008's rule, and
/// it matters as much on a read as on a write — a caller deciding whether to
/// start a checkout should not be branching on exception types.
/// </para>
/// <para>
/// <b>The shape survives becoming remote.</b> Nothing here names a type from
/// Catalog's model or from any persistence library, the response is a flat
/// record of primitives, and the call is asynchronous and cancellable. A
/// read-mostly catalogue is the easiest thing in this system to move behind a
/// replica or its own service, and the day that happens this interface is
/// implemented by an HTTP client instead and no consumer changes.
/// </para>
/// </remarks>
public interface IEventPricing
{
    /// <summary>
    /// Reads what one seat at an event costs, and the window in which it may be
    /// sold.
    /// </summary>
    Task<EventPricingResponse> GetAsync(
        EventPricingRequest request,
        CancellationToken cancellationToken = default);
}
