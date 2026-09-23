namespace Encore.Modules.Catalog.Contracts;

/// <summary>
/// What another module may ask Catalog about an event before selling it.
/// </summary>
/// <remarks>
/// Named for the caller's need, so it cannot grow into a general query surface. An
/// unknown event is a status, not an exception, and nothing here names Catalog's model,
/// so it can be served remotely later without changing callers.
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
