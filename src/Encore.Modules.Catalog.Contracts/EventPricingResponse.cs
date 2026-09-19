namespace Encore.Modules.Catalog.Contracts;

/// <summary>
/// What an event costs, and the window in which it may be sold.
/// </summary>
/// <remarks>
/// Everything but <paramref name="Status"/> is null when the event was not
/// found, so callers switch on the status first and unwrap afterwards — the
/// same shape the Inventory contracts use, and for the same reason: "only a
/// successful lookup carries a price" should be a fact of the type rather than
/// a convention each call site has to remember.
/// </remarks>
/// <param name="Status">Whether the event was found.</param>
/// <param name="UnitPrice">What one seat costs.</param>
/// <param name="Currency">ISO 4217 code for <paramref name="UnitPrice"/>.</param>
/// <param name="OnSaleAt">
/// When tickets become orderable, or <see langword="null"/> for immediately.
/// A caller refuses a checkout before this instant; Catalog states the fact and
/// does not enforce it, because it has no idea what a checkout is.
/// </param>
/// <param name="StartsAt">
/// When the show begins. Carried so a caller can tell a customer what they are
/// buying into, not as an upper bound on selling — sales stay open once a show
/// has started, because walk-up sales are real (DECISIONS 026).
/// </param>
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
