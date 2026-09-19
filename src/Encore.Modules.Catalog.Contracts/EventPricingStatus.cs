namespace Encore.Modules.Catalog.Contracts;

/// <summary>
/// How a pricing lookup turned out. A closed set, so callers can switch
/// exhaustively.
/// </summary>
public enum EventPricingStatus
{
    /// <summary>The event exists, and the response carries its price and sale window.</summary>
    Priced = 0,

    /// <summary>No event with that id. Nothing else on the response is set.</summary>
    EventNotFound = 1
}
