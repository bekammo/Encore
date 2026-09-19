using Encore.Modules.Catalog.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Encore.Modules.Catalog.Data;

/// <summary>
/// Serves <see cref="IEventPricing"/> from Catalog's own tables, in the
/// caller's process.
/// </summary>
/// <remarks>
/// <para>
/// It lives in <c>Data/</c> rather than an <c>InProcess/</c> folder of its own
/// because that is what it is — one projection over the DbContext. Inventory's
/// equivalent sits under <c>Adapters/</c> because Inventory has adapters;
/// Catalog has three folders and does not need a fourth to hold a single
/// query.
/// </para>
/// <para>
/// <c>internal</c>, so the only way to reach it is through the interface. A
/// consumer that could name this type could also decide to new one up, and the
/// registration in <c>CatalogModule</c> would stop being the single place the
/// implementation is chosen.
/// </para>
/// </remarks>
internal sealed class InProcessEventPricing(CatalogDbContext catalog) : IEventPricing
{
    private readonly CatalogDbContext _catalog = catalog;

    /// <inheritdoc />
    public async Task<EventPricingResponse> GetAsync(
        EventPricingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Projected rather than loaded: the caller needs four columns, and
        // materialising the entity would hand this module's model to a query
        // that only wants numbers.
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
