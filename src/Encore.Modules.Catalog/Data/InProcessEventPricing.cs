using Encore.Modules.Catalog.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Catalog.Data;

internal sealed class InProcessEventPricing(CatalogDbContext catalog) : IEventPricing
{
    private readonly CatalogDbContext _catalog = catalog;

    /// <inheritdoc />
    public async Task<EventPricingResponse> GetAsync(
        EventPricingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var priced = await _catalog.Events
            .AsNoTracking()
            .Where(show => show.Id == request.EventId)
            .Select(show => new
            {
                show.Price,
                show.Currency,
                show.OnSaleAt,
                show.StartsAt
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return priced is null
            ? EventPricingResponse.EventNotFound
            : EventPricingResponse.Priced(
                priced.Price,
                priced.Currency,
                priced.OnSaleAt,
                priced.StartsAt);
    }
}
