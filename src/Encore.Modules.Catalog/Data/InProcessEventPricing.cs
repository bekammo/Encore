using Encore.Modules.Catalog.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Catalog.Data;

/// <summary>
/// Serves <see cref="IEventPricing"/> from Catalog's own tables, in process. Internal, so
/// callers can only reach it through the interface.
/// </summary>
internal sealed class InProcessEventPricing(CatalogDbContext catalog) : IEventPricing
{
    private readonly CatalogDbContext _catalog = catalog;

    /// <inheritdoc />
    public async Task<EventPricingResponse> GetAsync(
        EventPricingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Projected: the caller needs four columns, not the entity.
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
