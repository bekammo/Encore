namespace Encore.Modules.Catalog.Contracts;

/// <summary>
/// Named for the caller's need, so it cannot grow into a general query surface (017). An unknown
/// event is a status, not an exception, and nothing here names Catalog's model, so it can be
/// served remotely without changing callers.
/// </summary>
public interface IEventPricing
{
    Task<EventPricingResponse> GetAsync(
        EventPricingRequest request,
        CancellationToken cancellationToken = default);
}
