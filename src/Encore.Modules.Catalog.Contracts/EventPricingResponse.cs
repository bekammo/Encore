namespace Encore.Modules.Catalog.Contracts;

/// <summary>
/// What an event costs, and the window in which it may be sold.
/// </summary>
/// <remarks>Everything but <paramref name="Status"/> is null when the event was not found.</remarks>
/// <param name="OnSaleAt">When tickets become orderable, or null for immediately. Catalog states it; Orders enforces it.</param>
/// <param name="StartsAt">When the show begins. Not an upper bound on sales: walk-up sales are real.</param>
public sealed record EventPricingResponse(
    EventPricingStatus Status,
    decimal? UnitPrice = null,
    string? Currency = null,
    DateTime? OnSaleAt = null,
    DateTime? StartsAt = null)
{
    /// <summary>The event exists and the response is fully populated.</summary>
    public static EventPricingResponse Priced(
        decimal unitPrice,
        string currency,
        DateTime? onSaleAt,
        DateTime startsAt) =>
        new(EventPricingStatus.Priced, unitPrice, currency, onSaleAt, startsAt);

    /// <summary>No such event.</summary>
    public static EventPricingResponse EventNotFound { get; } =
        new(EventPricingStatus.EventNotFound);
}
