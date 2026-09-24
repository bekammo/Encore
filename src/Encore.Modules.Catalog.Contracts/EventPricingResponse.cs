namespace Encore.Modules.Catalog.Contracts;

/// <summary>Everything but <paramref name="Status"/> is null when the event was not found.</summary>
/// <param name="OnSaleAt">Null for on sale immediately. Orders enforces it; Catalog only states it (009).</param>
/// <param name="StartsAt">Not a sales cutoff: walk-up sales are real (009).</param>
public sealed record EventPricingResponse(
    EventPricingStatus Status,
    decimal? UnitPrice = null,
    string? Currency = null,
    DateTime? OnSaleAt = null,
    DateTime? StartsAt = null)
{
    public static EventPricingResponse Priced(
        decimal unitPrice,
        string currency,
        DateTime? onSaleAt,
        DateTime startsAt) =>
        new(EventPricingStatus.Priced, unitPrice, currency, onSaleAt, startsAt);

    public static EventPricingResponse EventNotFound { get; } =
        new(EventPricingStatus.EventNotFound);
}
