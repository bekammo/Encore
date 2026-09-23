namespace Encore.Modules.Catalog.Models;

/// <summary>
/// A show that tickets are sold for. A plain POCO: there are no invariants to protect.
/// </summary>
public sealed class Event
{
    public Guid Id { get; set; }

    /// <summary>
    /// The venue hosting it. An id, not a navigation property.
    /// </summary>
    public Guid VenueId { get; set; }

    /// <summary>Display name of the show.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>When the show begins. Always UTC.</summary>
    public DateTime StartsAt { get; set; }

    /// <summary>
    /// When tickets become orderable, or null for immediately. Orders enforces it; sales
    /// stay open after the show starts.
    /// </summary>
    public DateTime? OnSaleAt { get; set; }

    /// <summary>
    /// What one seat costs. Orders copies it onto the order at checkout.
    /// </summary>
    public decimal Price { get; set; }

    /// <summary>
    /// ISO 4217 code, per event. One event per order means nothing ever converts.
    /// </summary>
    public string Currency { get; set; } = string.Empty;
}
